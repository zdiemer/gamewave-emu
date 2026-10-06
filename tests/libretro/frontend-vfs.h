/* Frontend VFS for tests: an OS-invisible virtual mount and partial I/O. */
#include <stdarg.h>
#include <sys/stat.h>
#ifdef _WIN32
#include <io.h>
#define file_seek _fseeki64
#define file_tell _ftelli64
#else
#include <dirent.h>
#define file_seek fseeko
#define file_tell ftello
#endif

static char virtual_root[4096], physical_root[4096];
static bool vfs_enabled, vfs_deny;
static unsigned vfs_open_files, vfs_open_dirs, vfs_reads, vfs_writes, vfs_renames, info_logs, error_logs;
#ifdef _WIN32
static DWORD frontend_thread;
#endif
static void RETRO_CALLCONV frontend_log(enum retro_log_level level, const char *format, ...)
{
    CHECK(strcmp(format,"%s\n") == 0);
#ifdef _WIN32
    CHECK(GetCurrentThreadId() == frontend_thread);
#endif
    char text[8192]; va_list arguments; va_start(arguments,format);
    vsnprintf(text,sizeof(text),format,arguments); va_end(arguments);
    CHECK(text[0]);
    if (level == RETRO_LOG_INFO) info_logs++;
    if (level == RETRO_LOG_ERROR) error_logs++;
}

static void normalize_path(char *path) { for (; *path; path++) if (*path == '\\') *path = '/'; }
static void translate_path(const char *path, char *result)
{
    CHECK(strlen(path) < 4096); strcpy(result,path); normalize_path(result);
    size_t length = strlen(virtual_root);
    if (length && strncmp(result,virtual_root,length) == 0 && (!result[length] || result[length] == '/')) {
        char suffix[4096]; strcpy(suffix,result+length);
        CHECK(strlen(physical_root) + strlen(suffix) < 4096);
        strcpy(result,physical_root); strcat(result,suffix);
    }
}
struct retro_vfs_file_handle { FILE *file; char path[4096]; };
static const char *RETRO_CALLCONV vfs_path(struct retro_vfs_file_handle *handle) { return handle->path; }
static struct retro_vfs_file_handle *RETRO_CALLCONV vfs_open(const char *path, unsigned mode, unsigned hints)
{
    (void)hints; if (vfs_deny) return NULL;
    char real[4096]; translate_path(path,real);
    FILE *file = fopen(real,(mode & RETRO_VFS_FILE_ACCESS_WRITE) ? "wb" : "rb");
    if (!file) return NULL;
    struct retro_vfs_file_handle *handle = (struct retro_vfs_file_handle *)calloc(1,sizeof(*handle)); CHECK(handle);
    handle->file = file; strcpy(handle->path,path); vfs_open_files++; return handle;
}
static int RETRO_CALLCONV vfs_close(struct retro_vfs_file_handle *handle)
{
    int result = fclose(handle->file); free(handle); vfs_open_files--; return result;
}
static int64_t RETRO_CALLCONV vfs_tell(struct retro_vfs_file_handle *handle) { return file_tell(handle->file); }
static int64_t RETRO_CALLCONV vfs_size(struct retro_vfs_file_handle *handle)
{
    int64_t position = file_tell(handle->file); CHECK(file_seek(handle->file,0,SEEK_END) == 0);
    int64_t size = file_tell(handle->file); CHECK(file_seek(handle->file,position,SEEK_SET) == 0); return size;
}
static int64_t RETRO_CALLCONV vfs_seek(struct retro_vfs_file_handle *handle, int64_t offset, int origin)
{
    return file_seek(handle->file,offset,origin); /* Deliberately returns success, not position. */
}
static int64_t RETRO_CALLCONV vfs_read(struct retro_vfs_file_handle *handle, void *buffer, uint64_t size)
{
    vfs_reads++; size_t count = fread(buffer,1,(size_t)(size > 11 ? 11 : size),handle->file);
    return ferror(handle->file) ? -1 : (int64_t)count;
}
static int64_t RETRO_CALLCONV vfs_write(struct retro_vfs_file_handle *handle, const void *buffer, uint64_t size)
{
    vfs_writes++; return (int64_t)fwrite(buffer,1,(size_t)(size > 7 ? 7 : size),handle->file);
}
static int RETRO_CALLCONV vfs_flush(struct retro_vfs_file_handle *handle) { return fflush(handle->file); }
static int RETRO_CALLCONV vfs_remove(const char *path) { char real[4096]; translate_path(path,real); return remove(real); }
static int RETRO_CALLCONV vfs_rename(const char *source, const char *destination)
{
    char from[4096], to[4096]; translate_path(source,from); translate_path(destination,to); vfs_renames++;
    /* Exercise the fallback for frontends that cannot replace an existing file. */
    FILE *existing = fopen(to,"rb"); if (existing) { fclose(existing); return -1; }
    return rename(from,to);
}
static int64_t RETRO_CALLCONV vfs_truncate(struct retro_vfs_file_handle *handle, int64_t size)
{
#ifdef _WIN32
    return _chsize_s(_fileno(handle->file),(uint64_t)size) == 0 ? size : -1;
#else
    return ftruncate(fileno(handle->file),size) == 0 ? size : -1;
#endif
}
static int RETRO_CALLCONV vfs_stat(const char *path, int32_t *size)
{
    if (vfs_deny) return 0;
    char real[4096]; translate_path(path,real);
#ifdef _WIN32
    struct _stat64 info; if (_stat64(real,&info) != 0) return 0;
    bool directory = (info.st_mode & _S_IFDIR) != 0;
#else
    struct stat info; if (stat(real,&info) != 0) return 0;
    bool directory = S_ISDIR(info.st_mode);
#endif
    *size = info.st_size > INT32_MAX ? INT32_MAX : (int32_t)info.st_size;
    return RETRO_VFS_STAT_IS_VALID | (directory ? RETRO_VFS_STAT_IS_DIRECTORY : 0);
}
static int RETRO_CALLCONV vfs_mkdir(const char *path) { char real[4096]; translate_path(path,real); return mkdir_one(real); }
struct retro_vfs_dir_handle {
#ifdef _WIN32
    HANDLE directory; WIN32_FIND_DATAA entry; bool first;
#else
    DIR *directory; struct dirent *entry; char path[4096];
#endif
};
static struct retro_vfs_dir_handle *RETRO_CALLCONV vfs_opendir(const char *path, bool hidden)
{
    (void)hidden; if (vfs_deny) return NULL;
    char real[4096]; translate_path(path,real);
    struct retro_vfs_dir_handle *handle = (struct retro_vfs_dir_handle *)calloc(1,sizeof(*handle)); CHECK(handle);
#ifdef _WIN32
    char pattern[4096]; join_path(pattern,sizeof(pattern),real,"*");
    handle->directory = FindFirstFileA(pattern,&handle->entry); handle->first = true;
    if (handle->directory == INVALID_HANDLE_VALUE) { free(handle); return NULL; }
#else
    handle->directory = opendir(real); strcpy(handle->path,real);
    if (!handle->directory) { free(handle); return NULL; }
#endif
    vfs_open_dirs++; return handle;
}
static bool RETRO_CALLCONV vfs_readdir(struct retro_vfs_dir_handle *handle)
{
#ifdef _WIN32
    if (handle->first) { handle->first = false; return true; }
    return FindNextFileA(handle->directory,&handle->entry) != 0;
#else
    handle->entry = readdir(handle->directory); return handle->entry != NULL;
#endif
}
static const char *RETRO_CALLCONV vfs_dirname(struct retro_vfs_dir_handle *handle)
{
#ifdef _WIN32
    return handle->entry.cFileName;
#else
    return handle->entry->d_name;
#endif
}
static bool RETRO_CALLCONV vfs_isdir(struct retro_vfs_dir_handle *handle)
{
#ifdef _WIN32
    return (handle->entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
#else
    char path[4096]; join_path(path,sizeof(path),handle->path,handle->entry->d_name);
    struct stat info; CHECK(stat(path,&info) == 0); return S_ISDIR(info.st_mode);
#endif
}
static int RETRO_CALLCONV vfs_closedir(struct retro_vfs_dir_handle *handle)
{
#ifdef _WIN32
    int result = FindClose(handle->directory) ? 0 : -1;
#else
    int result = closedir(handle->directory);
#endif
    free(handle); vfs_open_dirs--; return result;
}
static struct retro_vfs_interface frontend_vfs = {
    .get_path = vfs_path, .open = vfs_open, .close = vfs_close, .size = vfs_size, .tell = vfs_tell,
    .seek = vfs_seek, .read = vfs_read, .write = vfs_write, .flush = vfs_flush,
    .remove = vfs_remove, .rename = vfs_rename, .truncate = vfs_truncate,
    .stat = vfs_stat, .mkdir = vfs_mkdir, .opendir = vfs_opendir, .readdir = vfs_readdir,
    .dirent_get_name = vfs_dirname, .dirent_is_dir = vfs_isdir, .closedir = vfs_closedir,
};
