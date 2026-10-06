/* Native frontend smoke test. Generates a tiny original Game Wave disc fixture. */
#define _CRT_SECURE_NO_WARNINGS
#define _DEFAULT_SOURCE
#define RETRO_IMPORT_SYMBOLS
#include "vendor/libretro.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#ifdef _WIN32
#include <windows.h>
#include <direct.h>
#include <process.h>
#define mkdir_one(path) _mkdir(path)
#define pause_ms(ms) Sleep(ms)
typedef HMODULE library_t;
static library_t open_library(const char *path) { return LoadLibraryA(path); }
static void *symbol(library_t lib, const char *name) { return (void *)GetProcAddress(lib, name); }
static void close_library(library_t lib) { FreeLibrary(lib); }
#else
#include <dlfcn.h>
#include <sys/stat.h>
#include <unistd.h>
#include <sys/wait.h>
#define mkdir_one(path) mkdir(path, 0755)
#define pause_ms(ms) usleep((ms) * 1000)
typedef void *library_t;
static library_t open_library(const char *path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static void *symbol(library_t lib, const char *name) { return dlsym(lib, name); }
static void close_library(library_t lib) { dlclose(lib); }
#endif

#define CHECK(condition) do { if (!(condition)) { fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); exit(1); } } while (0)
static void join_path(char *destination, size_t capacity, const char *directory, const char *file)
{
    size_t length = strlen(directory), tail = strlen(file);
    CHECK(length + tail + 2 <= capacity);
    memcpy(destination, directory, length);
    destination[length] = '/';
    memcpy(destination + length + 1, file, tail + 1);
}
static char save_directory[4096];
static struct retro_disk_control_callback disk;
static struct retro_keyboard_callback keyboard;
static unsigned video_calls, poll_calls, audio_calls, audio_samples, messages;
static int pixel_x, pixel_y, buttons[6];
static bool reject_pixel, options_changed, heard_audio;
static const char *keyboard_remote = "1";

static bool RETRO_CALLCONV environment(unsigned cmd, void *data)
{
    switch (cmd) {
        case RETRO_ENVIRONMENT_SET_SUPPORT_NO_GAME: CHECK(!*(bool *)data); return true;
        case RETRO_ENVIRONMENT_SET_SERIALIZATION_QUIRKS:
            CHECK(*(uint64_t *)data == RETRO_SERIALIZATION_QUIRK_INCOMPLETE); return true;
        case RETRO_ENVIRONMENT_SET_PIXEL_FORMAT:
            CHECK(*(enum retro_pixel_format *)data == RETRO_PIXEL_FORMAT_XRGB8888);
            return !reject_pixel;
        case RETRO_ENVIRONMENT_GET_SAVE_DIRECTORY: *(const char **)data = save_directory; return true;
        case RETRO_ENVIRONMENT_SET_DISK_CONTROL_INTERFACE: disk = *(struct retro_disk_control_callback *)data; return true;
        case RETRO_ENVIRONMENT_SET_KEYBOARD_CALLBACK: keyboard = *(struct retro_keyboard_callback *)data; return true;
        case RETRO_ENVIRONMENT_SET_VARIABLES: {
            const struct retro_variable *v = (const struct retro_variable *)data;
            CHECK(v[0].key && v[1].key && !v[2].key); return true;
        }
        case RETRO_ENVIRONMENT_GET_VARIABLE: {
            struct retro_variable *v = (struct retro_variable *)data;
            v->value = strcmp(v->key, "gamewave_keyboard_remote") == 0 ? keyboard_remote : "blend";
            return true;
        }
        case RETRO_ENVIRONMENT_GET_VARIABLE_UPDATE: *(bool *)data = options_changed; options_changed = false; return true;
        case RETRO_ENVIRONMENT_SET_CONTROLLER_INFO: {
            const struct retro_controller_info *p = (const struct retro_controller_info *)data;
            for (int i = 0; i < 6; i++) CHECK(p[i].num_types == 1 && p[i].types[0].id == RETRO_DEVICE_JOYPAD);
            CHECK(!p[6].types); return true;
        }
        case RETRO_ENVIRONMENT_SET_INPUT_DESCRIPTORS: {
            const struct retro_input_descriptor *d = (const struct retro_input_descriptor *)data;
            for (int i = 0; i < 96; i++) CHECK(d[i].description && d[i].port < 6);
            CHECK(!d[96].description); return true;
        }
        case RETRO_ENVIRONMENT_SET_MESSAGE:
            fprintf(stderr, "core: %s\n", ((const struct retro_message *)data)->msg); messages++; return true;
        default: return false;
    }
}

static void RETRO_CALLCONV video(const void *data, unsigned width, unsigned height, size_t pitch)
{
    CHECK(data && width == 720 && height == 480 && pitch == 720 * 4);
    video_calls++;
    pixel_x = pixel_y = -1;
    for (unsigned y = 0; y < height; y++) {
        const uint32_t *row = (const uint32_t *)((const char *)data + y * pitch);
        for (unsigned x = 0; x < width; x++)
            if ((row[x] & 0xffffff) != 0) { pixel_x = (int)x; pixel_y = (int)y; return; }
    }
}
static size_t RETRO_CALLCONV audio_batch(const int16_t *samples, size_t frames)
{
    CHECK(frames == 735);
    audio_calls++;
    for (size_t i = 0; i < frames * 2; i++)
        if (samples[i]) { CHECK(samples[i] == 16384); heard_audio = true; }
    return frames;
}
static void RETRO_CALLCONV audio_sample(int16_t left, int16_t right) { CHECK(left == right); audio_samples++; }
static void RETRO_CALLCONV poll(void) { poll_calls++; }
static int16_t RETRO_CALLCONV input(unsigned port, unsigned device, unsigned index, unsigned id)
{
    CHECK(port < 6 && device == RETRO_DEVICE_JOYPAD && index == 0 && id < 16);
    return (buttons[port] & (1 << id)) != 0;
}

static void put32(FILE *f, uint32_t n)
{
    for (int i = 0; i < 4; i++) { fputc(n & 255, f); n >>= 8; }
}
static void put_string(FILE *f, const char *s) { put32(f, (uint32_t)strlen(s) + 1); fwrite(s, 1, strlen(s) + 1, f); }
/* A zlib stream with a single stored DEFLATE block, to avoid a test dependency. */
static void zlib_stored(FILE *f, const unsigned char *raw, unsigned size)
{
    uint32_t a = 1, b = 0;
    CHECK(size <= 65535);
    fputc(0x78, f); fputc(1, f); fputc(1, f);
    fputc(size & 255, f); fputc(size >> 8, f);
    fputc((~size) & 255, f); fputc(((~size) >> 8) & 255, f);
    fwrite(raw, 1, size, f);
    for (unsigned i = 0; i < size; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
    uint32_t adler = (b << 16) | a;
    for (int i = 3; i >= 0; i--) fputc((adler >> (i * 8)) & 255, f);
}

static uint32_t code[256];
static unsigned code_count;
static void abc(unsigned op, unsigned a, unsigned b, unsigned c) { code[code_count++] = op | (a << 24) | (b << 15) | (c << 6); }
static void abx(unsigned op, unsigned a, unsigned bx) { code[code_count++] = op | (a << 24) | (bx << 6); }
static void method(unsigned module, unsigned name) { abx(5, 0, module); abc(6, 0, 0, 250 + name); }
static void constant(unsigned reg, unsigned k) { abx(1, reg, k); }
static void move(unsigned dst, unsigned src) { abc(0, dst, src, 0); }
static void call(unsigned args, unsigned results) { abc(25, 0, args + 1, results + 1); }

static void fixture(const char *directory, bool opens_tray)
{
    char path[4096]; FILE *f;
    mkdir_one(directory);
    join_path(path, sizeof(path), directory, "gamewave.diz");
    f = fopen(path, "wb"); CHECK(f);
    fputs("[global]\nappname=Libretro smoke\nappfile=/game.zbc\n", f); fclose(f);
    join_path(path, sizeof(path), directory, "test.zbm");
    f = fopen(path, "wb"); CHECK(f);
    const uint32_t header[] = {1, 1, 100, 4, 1, 1, 0, 0, 1, 15, 4, 0};
    for (unsigned i = 0; i < 12; i++) put32(f, header[i]);
    const unsigned char white[] = {255, 235, 128, 128}; zlib_stored(f, white, 4); fclose(f);
    join_path(path, sizeof(path), directory, "test.zwf");
    f = fopen(path, "wb"); CHECK(f);
    put32(f, 0x7c90ee02); put32(f, 1); put32(f, 1); put32(f, 13); put32(f, 0);
    const unsigned char pcm[] = {0x40, 0}; zlib_stored(f, pcm, 2); fclose(f);

    const char *strings[] = {"gl", "LoadTexture", "test.zbm", NULL, "CreateOverlayFromTexture", "SetPosition",
        "audio", "Load", "test.zwf", "Play", NULL, "input", "WaitForKey", "eeprom", "SaveGameToNewSlot",
        NULL, "Smoke", "Slot", "DATA", "time", "Sleep", NULL, "engine", "OpenTray", "SetVisibility"};
    code_count = 0;
    if (opens_tray) { method(22, 23); call(0, 0); abc(27, 0, 1, 0); }
    else {
        method(0, 1); constant(1, 3); constant(2, 2); call(2, 1);
        move(1, 0); method(0, 4); call(1, 1); move(10, 0);
        method(0, 24); move(1, 10); constant(2, 10); call(2, 0);
        method(6, 7); constant(1, 3); constant(2, 8); call(2, 1);
        move(1, 0); method(6, 9); constant(2, 10); call(2, 0);
        method(13, 14); constant(1, 10); constant(2, 15); constant(3, 16); constant(4, 17); constant(5, 18); call(5, 0);
        unsigned loop = code_count;
        method(11, 12); call(0, 3); move(11, 0); move(12, 1);
        method(0, 5); move(1, 10); move(2, 11); move(3, 12); call(3, 0);
        abx(20, 0, 131071 + loop - code_count - 1);
    }
    join_path(path, sizeof(path), directory, "game.zbc");
    f = fopen(path, "wb"); CHECK(f);
    const unsigned char zbc[] = {0x1b,'Z','B','C',0x0a,0x1a,0x50,1,0,1,1,4,4,4,6,8,9,9,4};
    fwrite(zbc, 1, sizeof(zbc), f); put32(f, 31415926);
    put_string(f, "smoke"); put32(f, 0); fputc(0,f); fputc(0,f); fputc(0,f); fputc(16,f);
    put32(f,0); put32(f,0); put32(f,0); put32(f,25);
    for (unsigned i = 0; i < 25; i++) {
        if (strings[i]) { fputc(4,f); put_string(f,strings[i]); }
        else { fputc(3,f); put32(f, i == 3 ? 0 : i == 10 ? 1 : i == 15 ? 4 : 1000); }
    }
    put32(f,0); put32(f,code_count);
    for (unsigned i = 0; i < code_count; i++) put32(f,code[i]);
    fclose(f);
}

static void (*p_init)(void), (*p_deinit)(void), (*p_run)(void), (*p_reset)(void), (*p_unload)(void);
static unsigned (*p_version)(void);
static bool (*p_load)(const struct retro_game_info *);
static void (*p_info)(struct retro_system_info *);
static void (*p_av)(struct retro_system_av_info *);
static void (*p_environment)(retro_environment_t);
static void (*p_video)(retro_video_refresh_t);
static void (*p_audio)(retro_audio_sample_t);
static void (*p_audio_batch)(retro_audio_sample_batch_t);
static void (*p_poll)(retro_input_poll_t);
static void (*p_input)(retro_input_state_t);
static void (*p_device)(unsigned, unsigned);
static size_t (*p_serialize_size)(void);
static bool (*p_serialize)(void *, size_t), (*p_unserialize)(const void *, size_t);
#define LOAD(field, name) do { void *address = symbol(lib, name); CHECK(address); memcpy(&field, &address, sizeof(field)); } while(0)
static void load_api(library_t lib)
{
    LOAD(p_init,"retro_init"); LOAD(p_deinit,"retro_deinit"); LOAD(p_run,"retro_run"); LOAD(p_reset,"retro_reset");
    LOAD(p_unload,"retro_unload_game"); LOAD(p_version,"retro_api_version"); LOAD(p_load,"retro_load_game");
    LOAD(p_info,"retro_get_system_info"); LOAD(p_av,"retro_get_system_av_info"); LOAD(p_environment,"retro_set_environment");
    LOAD(p_video,"retro_set_video_refresh"); LOAD(p_audio,"retro_set_audio_sample"); LOAD(p_audio_batch,"retro_set_audio_sample_batch");
    LOAD(p_poll,"retro_set_input_poll"); LOAD(p_input,"retro_set_input_state"); LOAD(p_device,"retro_set_controller_port_device");
    LOAD(p_serialize_size,"retro_serialize_size"); LOAD(p_serialize,"retro_serialize"); LOAD(p_unserialize,"retro_unserialize");
    CHECK(symbol(lib,"retro_get_region") && symbol(lib,"retro_load_game_special") && symbol(lib,"retro_get_memory_data") &&
          symbol(lib,"retro_get_memory_size") && symbol(lib,"retro_cheat_reset") && symbol(lib,"retro_cheat_set"));
}

int main(int argc, char **argv)
{
    CHECK(argc == 3 || (argc == 4 && strcmp(argv[3],"--restore") == 0));
    char content[4096], tray[4096], playlist[4096], save[4096], state_path[4096];
    join_path(state_path,sizeof(state_path),argv[2],"portable.state");
    join_path(save_directory,sizeof(save_directory),argv[2],"saves");
    join_path(content,sizeof(content),argv[2],"disc");
    join_path(tray,sizeof(tray),argv[2],"tray");
    join_path(playlist,sizeof(playlist),argv[2],"discs.m3u");
    join_path(save,sizeof(save),save_directory,"gamewave/gamewave.saves");
    mkdir_one(argv[2]); fixture(content,false); fixture(tray,true);
    FILE *f = fopen(playlist,"wb"); CHECK(f); fputs("# Two-disc test\ndisc/gamewave.diz\ntray/gamewave.diz\n",f); fclose(f);
    library_t lib = open_library(argv[1]); CHECK(lib); load_api(lib);
    CHECK(p_version() == RETRO_API_VERSION);
    struct retro_system_info info = {0}; p_info(&info);
    CHECK(strcmp(info.library_name,"gamewave") == 0 && info.need_fullpath && info.block_extract);
    CHECK(strcmp(info.valid_extensions,"iso|zip|m3u|diz") == 0);
    p_environment(environment); p_video(video); p_audio(audio_sample); p_audio_batch(audio_batch); p_poll(poll); p_input(input); p_init();
    CHECK(disk.set_eject_state && keyboard.callback);
    CHECK(!p_load(NULL));
    struct retro_game_info game = {"missing.iso",NULL,0,NULL}; CHECK(!p_load(&game));
    unsigned expected_messages = messages;
    game.path = playlist; reject_pixel = true; CHECK(!p_load(&game)); reject_pixel = false; expected_messages++;
    CHECK(p_load(&game));
    size_t state_size = p_serialize_size(); CHECK(state_size > 0 && state_size <= 128 * 1024 * 1024);
    unsigned char *state = (unsigned char *)malloc(state_size); CHECK(state);
    if (argc == 4) {
        f = fopen(state_path,"rb"); CHECK(f); CHECK(fread(state,1,state_size,f) == state_size); fclose(f);
        CHECK(p_unserialize(state,state_size)); p_run(); CHECK(pixel_x == 7 && pixel_y == 6);
        keyboard.callback(true,RETROK_3,0,0); p_run(); CHECK(pixel_x == 3 && pixel_y == 1);
        p_unload(); p_deinit(); close_library(lib); free(state);
        puts("PASS: restored a portable state in a fresh process"); return 0;
    }
    struct retro_system_av_info av = {0}; p_av(&av);
    CHECK(av.geometry.base_width == 720 && av.geometry.base_height == 480 && av.geometry.aspect_ratio > 1.33f && av.geometry.aspect_ratio < 1.34f);
    CHECK(av.timing.fps == 60 && av.timing.sample_rate == 44100);
    CHECK(!p_serialize(NULL,0) && !p_unserialize(NULL,0));
    CHECK(!p_serialize(state,state_size-1));
    CHECK(p_serialize(state,state_size)); CHECK(p_unserialize(state,state_size)); // Before first retro_run.
    CHECK(disk.get_num_images() == 2 && !disk.get_eject_state() && !disk.set_image_index(1));
    for (int i = 0; i < 60; i++) p_run();
    fprintf(stderr,"frames=%u polls=%u audio=%u heard=%d pixel=%d,%d\n",video_calls,poll_calls,audio_calls,heard_audio,pixel_x,pixel_y);
    CHECK(video_calls == 60 && poll_calls == 60 && audio_calls == 60 && heard_audio && pixel_x == 0 && pixel_y == 0);
    f = fopen(save,"rb"); CHECK(f); char magic[8]; CHECK(fread(magic,1,8,f) == 8 && memcmp(magic,"GWSAVE1\0",8) == 0); fclose(f);
    for (int port = 0; port < 6; port++) {
        buttons[port] = 1 << RETRO_DEVICE_ID_JOYPAD_A; p_run();
        CHECK(pixel_x == 16 && pixel_y == port + 1);
        buttons[port] = 0; p_run();
    }
    const unsigned number_buttons[] = {11,8,0,9,1,4,7,5,6,10};
    for (int digit = 9; digit >= 0; digit--) {
        buttons[0] = (1 << RETRO_DEVICE_ID_JOYPAD_R2) | (1 << number_buttons[digit]); p_run();
        CHECK(pixel_x == digit && pixel_y == 1); buttons[0] = 0; p_run();
    }
    p_device(5,RETRO_DEVICE_NONE); buttons[5] = 1 << RETRO_DEVICE_ID_JOYPAD_B; p_run();
    CHECK(pixel_x == 0 && pixel_y == 1); buttons[5] = 0; p_device(5,RETRO_DEVICE_JOYPAD);
    keyboard_remote = "6"; options_changed = true; p_run(); keyboard.callback(true,RETROK_7,0,0); p_run();
    CHECK(pixel_x == 7 && pixel_y == 6);
    keyboard.callback(true,RETROK_a,0,RETROKMOD_CTRL); p_run(); CHECK(pixel_x == 7 && pixel_y == 6);
    p_audio_batch(NULL); unsigned previous_samples = audio_samples; p_run(); CHECK(audio_samples - previous_samples == 735); p_audio_batch(audio_batch);
    pause_ms(50); p_run(); CHECK(pixel_x == 7 && pixel_y == 6);
    CHECK(p_serialize_size() == state_size && p_serialize(state,state_size));
    f = fopen(state_path,"wb"); CHECK(f); CHECK(fwrite(state,1,state_size,f) == state_size); fclose(f);
    keyboard.callback(true,RETROK_2,0,0); p_run(); CHECK(pixel_x == 2 && pixel_y == 6);
    unsigned char *second = (unsigned char *)malloc(state_size); CHECK(second && p_serialize(second,state_size));
    CHECK(p_unserialize(state,state_size)); p_run(); CHECK(pixel_x == 7 && pixel_y == 6);
    CHECK(p_unserialize(second,state_size)); p_run(); CHECK(pixel_x == 2 && pixel_y == 6); free(second);
    state[44] ^= 1; CHECK(!p_unserialize(state,state_size)); state[44] ^= 1; expected_messages++;
    CHECK(!p_unserialize(state,100)); expected_messages++;
    p_run(); CHECK(pixel_x == 2 && pixel_y == 6);
    p_reset(); p_run(); CHECK(pixel_x == 0 && pixel_y == 0);
    CHECK(p_unserialize(state,state_size)); p_run(); CHECK(pixel_x == 7 && pixel_y == 6);
    CHECK(disk.set_eject_state(true)); CHECK(disk.set_image_index(1)); CHECK(disk.set_eject_state(false)); p_run();
    CHECK(disk.get_eject_state()); /* The fixture opened its own tray. */
    CHECK(disk.set_image_index(0)); CHECK(disk.set_eject_state(false)); p_run(); CHECK(pixel_x == 0 && pixel_y == 0);
    CHECK(disk.set_eject_state(true)); CHECK(disk.add_image_index()); CHECK(disk.get_num_images() == 3);
    struct retro_game_info invalid_replacement = {NULL,NULL,0,NULL};
    CHECK(!disk.replace_image_index(2,&invalid_replacement)); CHECK(disk.get_num_images() == 3);
    CHECK(disk.set_image_index(UINT32_MAX)); CHECK(disk.get_image_index() == 3);
    CHECK(disk.set_image_index(3)); CHECK(!disk.set_eject_state(false)); CHECK(disk.set_image_index(2)); CHECK(!disk.set_eject_state(false));
    struct retro_game_info replacement = {content,NULL,0,NULL}; CHECK(disk.replace_image_index(2,&replacement));
    CHECK(disk.set_eject_state(false)); p_run(); CHECK(pixel_x == 0 && pixel_y == 0);
    CHECK(disk.set_eject_state(true)); CHECK(disk.replace_image_index(2,NULL)); CHECK(disk.set_image_index(0)); CHECK(disk.set_eject_state(false));
    p_unload(); CHECK(disk.get_num_images() == 0); CHECK(p_load(&game)); p_run(); p_unload();
    CHECK(p_serialize_size() == 0 && !p_unserialize(state,state_size));
    CHECK(messages == expected_messages);
    p_deinit(); close_library(lib);
    /* Frontends unload and reload cores. Native AOT must remain resident safely. */
    lib = open_library(argv[1]); CHECK(lib); load_api(lib); p_init(); CHECK(p_load(&game));
    CHECK(p_unserialize(state,state_size)); p_run(); CHECK(pixel_x == 7 && pixel_y == 6);
    p_unload(); p_deinit(); close_library(lib); free(state);
#ifdef _WIN32
    CHECK(_spawnl(_P_WAIT,argv[0],argv[0],argv[1],argv[2],"--restore",NULL) == 0);
#else
    pid_t child = fork(); CHECK(child >= 0);
    if (child == 0) { execl(argv[0],argv[0],argv[1],argv[2],"--restore",(char *)NULL); _exit(1); }
    int status; CHECK(waitpid(child,&status,0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0);
#endif
    puts("PASS: native exports, lifecycle, video, audio, six remotes, keyboard, portable states, saves, playlists and disc swapping");
    return 0;
}
