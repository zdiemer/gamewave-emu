/* Optional retail-content probe. Usage: content CORE OUTPUT_DIR DISC [FRAMES]. */
#define main smoke_main
#include "smoke.c"
#undef main
#ifndef _WIN32
#include <time.h>
#endif

static uint32_t last_picture[720 * 480];
static unsigned picture_changes, audible_frames;
static void RETRO_CALLCONV content_video(const void *data, unsigned width, unsigned height, size_t pitch)
{
    CHECK(data && width == 720 && height == 480 && pitch == 720 * 4);
    if (memcmp(last_picture, data, sizeof(last_picture)) != 0) picture_changes++;
    memcpy(last_picture, data, sizeof(last_picture));
    video_calls++;
}
static size_t RETRO_CALLCONV content_audio(const int16_t *data, size_t frames)
{
    CHECK(frames == 735);
    for (size_t i = 0; i < frames * 2; i++)
        if (data[i] != 0) { audible_frames++; break; }
    audio_calls++;
    return frames;
}
static uint64_t milliseconds(void)
{
#ifdef _WIN32
    return GetTickCount64();
#else
    struct timespec t;
    CHECK(clock_gettime(CLOCK_MONOTONIC, &t) == 0);
    return (uint64_t)t.tv_sec * 1000 + (uint64_t)t.tv_nsec / 1000000;
#endif
}
static void picture(const char *directory, unsigned frame)
{
    char filename[80], path[4096];
    snprintf(filename, sizeof(filename), "frame-%u.bmp", frame);
    join_path(path, sizeof(path), directory, filename);
    FILE *f = fopen(path, "wb"); CHECK(f);
    /* Bottom-up 24-bit BMP, with an aligned 720-pixel row. */
    fputc('B',f); fputc('M',f); put32(f,54 + 720 * 480 * 3); put32(f,0); put32(f,54);
    put32(f,40); put32(f,720); put32(f,480); fputc(1,f); fputc(0,f); fputc(24,f); fputc(0,f);
    put32(f,0); put32(f,720 * 480 * 3); put32(f,0); put32(f,0); put32(f,0); put32(f,0);
    for (int y = 479; y >= 0; y--)
        for (int x = 0; x < 720; x++) {
            uint32_t color = last_picture[y * 720 + x];
            fputc(color & 255,f); fputc((color >> 8) & 255,f); fputc((color >> 16) & 255,f);
        }
    fclose(f);
}
int main(int argc, char **argv)
{
    CHECK(argc == 4 || argc == 5);
    unsigned frames = argc == 5 ? (unsigned)strtoul(argv[4],NULL,10) : 3600;
    CHECK(frames >= 600 && frames <= 36000);
    mkdir_one(argv[2]); join_path(save_directory,sizeof(save_directory),argv[2],"saves");
    library_t lib = open_library(argv[1]); CHECK(lib); load_api(lib);
    p_environment(environment); p_video(content_video); p_audio_batch(content_audio); p_poll(poll); p_input(input); p_init();
    struct retro_game_info game = {argv[3],NULL,0,NULL}; CHECK(p_load(&game));
    uint64_t start = milliseconds();
    for (unsigned frame = 0; frame < frames; frame++) {
        /* Skip intro / join from the red remote after allowing the initial movies. */
        buttons[0] = frame == 2400 || frame == 3000 ? 1 << RETRO_DEVICE_ID_JOYPAD_START : 0;
        p_run(); CHECK(messages == 0);
        if ((frame + 1) % 600 == 0) {
            picture(argv[2],frame + 1);
            printf("frame=%u picture_changes=%u audible_frames=%u\n",frame + 1,picture_changes,audible_frames); fflush(stdout);
        }
        uint64_t due = start + (frame + 1) * 1000ULL / 60;
        uint64_t now = milliseconds();
        if (due > now) pause_ms((unsigned)(due - now));
    }
    picture(argv[2],frames);
    CHECK(video_calls == frames && audio_calls == frames && picture_changes > 30 && audible_frames > 30);
    p_unload(); p_deinit(); close_library(lib);
    puts("PASS: retail content produced changing video and audio without core errors");
    return 0;
}
