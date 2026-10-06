/* Byte-exact rewind/runahead probe. Usage: determinism CORE OUTPUT_DIR [DISC]. */
#define main smoke_main
#include "smoke.c"
#undef main
#ifndef _WIN32
#include <time.h>
#endif

static uint64_t video_hash, audio_hash;
static unsigned av_enable = 3;
static int state_context;
static bool RETRO_CALLCONV deterministic_environment(unsigned cmd, void *data)
{
    if (cmd == RETRO_ENVIRONMENT_GET_AUDIO_VIDEO_ENABLE) { *(unsigned *)data = av_enable; return true; }
    if (cmd == RETRO_ENVIRONMENT_GET_SAVESTATE_CONTEXT) { *(int *)data = state_context; return true; }
    return environment(cmd,data);
}
static uint64_t hash_bytes(const void *data, size_t count)
{
    const unsigned char *bytes = (const unsigned char *)data;
    uint64_t hash = UINT64_C(14695981039346656037);
    for (size_t i = 0; i < count; i++) hash = (hash ^ bytes[i]) * UINT64_C(1099511628211);
    return hash;
}
static uint64_t save_hash(void)
{
    char path[4096]; join_path(path,sizeof(path),save_directory,"gamewave/gamewave.saves");
    FILE *file = fopen(path,"rb");
    if (!file) return 0;
    unsigned char bytes[4096]; size_t count; uint64_t hash = UINT64_C(14695981039346656037);
    while ((count = fread(bytes,1,sizeof(bytes),file)) != 0)
        for (size_t i=0; i<count; i++) hash = (hash ^ bytes[i]) * UINT64_C(1099511628211);
    CHECK(!ferror(file)); fclose(file); return hash;
}
static void RETRO_CALLCONV deterministic_video(const void *data, unsigned w, unsigned h, size_t pitch)
{
    CHECK(w == 720 && h == 480 && pitch == 720 * 4);
    CHECK(data || !(av_enable & 1));
    if (data) video_hash = hash_bytes(data, pitch * h);
    video_calls++;
}
static size_t RETRO_CALLCONV deterministic_audio(const int16_t *data, size_t frames)
{
    CHECK(frames == 735);
    audio_hash = hash_bytes(data, frames * 4); audio_calls++; return frames;
}
static uint64_t now_ms(void)
{
#ifdef _WIN32
    return GetTickCount64();
#else
    struct timespec t; CHECK(clock_gettime(CLOCK_MONOTONIC,&t) == 0);
    return (uint64_t)t.tv_sec * 1000 + (uint64_t)t.tv_nsec / 1000000;
#endif
}
int main(int argc, char **argv)
{
    CHECK(argc == 3 || argc == 4);
    mkdir_one(argv[2]); join_path(save_directory,sizeof(save_directory),argv[2],"saves");
    char disc[4096]; join_path(disc,sizeof(disc),argv[2],"disc");
    if (argc == 3) fixture(disc,false);
    library_t lib = open_library(argv[1]); CHECK(lib); load_api(lib);
    p_environment(deterministic_environment); p_video(deterministic_video); p_audio_batch(deterministic_audio);
    p_poll(poll); p_input(input); p_init();
    struct retro_game_info game = {argc == 4 ? argv[3] : disc,NULL,0,NULL}; CHECK(p_load(&game));
    size_t size = p_serialize_size();
    unsigned char *saved = (unsigned char *)malloc(size), *expected = (unsigned char *)malloc(size), *actual = (unsigned char *)malloc(size);
    CHECK(saved && expected && actual);
    const unsigned checkpoints[] = {0,11,60,299,600,1800,2999};
    unsigned position = 0;
    for (unsigned c = 0; c < sizeof(checkpoints)/sizeof(checkpoints[0]); c++) {
        unsigned target = checkpoints[c];
        while (position < target) { p_run(); CHECK(messages == 0); position++; }
        uint64_t start = now_ms(); CHECK(p_serialize(saved,size));
        printf("checkpoint=%u serialize_ms=%llu\n",target,(unsigned long long)(now_ms()-start)); fflush(stdout);
        uint64_t pictures[90], samples[90];
        for (unsigned f = 0; f < 90; f++) {
            buttons[0] = f == 3 || f == 70 ? 1 << RETRO_DEVICE_ID_JOYPAD_START : 0;
            p_run(); CHECK(messages == 0); pictures[f] = video_hash; samples[f] = audio_hash;
        }
        CHECK(p_serialize(expected,size));
        state_context = 1; CHECK(p_unserialize(saved,size)); state_context = 0;
        for (unsigned f = 0; f < 90; f++) {
            buttons[0] = f == 3 || f == 70 ? 1 << RETRO_DEVICE_ID_JOYPAD_START : 0;
            p_run(); CHECK(messages == 0);
            if (pictures[f] != video_hash || samples[f] != audio_hash) {
                fprintf(stderr,"diverged checkpoint=%u frame=%u video=%016llx/%016llx audio=%016llx/%016llx\n",target,f,
                    (unsigned long long)pictures[f],(unsigned long long)video_hash,(unsigned long long)samples[f],(unsigned long long)audio_hash);
                exit(1);
            }
        }
        CHECK(p_serialize(actual,size)); CHECK(memcmp(expected,actual,size) == 0);
        state_context = 1; CHECK(p_unserialize(saved,size));
        /* Single-instance runahead: preserve one committed frame while showing its successor. */
        for (unsigned f = 0; f < 90; f++) {
            buttons[0] = f == 3 || f == 70 ? 1 << RETRO_DEVICE_ID_JOYPAD_START : 0;
            buttons[5] = 0;
            av_enable = 2; p_run(); CHECK(audio_hash == samples[f]); CHECK(messages == 0);
            CHECK(p_serialize(actual,size));
            uint64_t flash = save_hash();
            unsigned previous_audio = audio_calls, previous_video = video_calls;
            buttons[5] = 1 << RETRO_DEVICE_ID_JOYPAD_B;
            av_enable = 0; p_run(); CHECK(audio_calls == previous_audio && video_calls == previous_video + 1);
            buttons[5] = 1 << RETRO_DEVICE_ID_JOYPAD_A;
            av_enable = 1; p_run(); CHECK(audio_calls == previous_audio && video_calls == previous_video + 2);
            CHECK(p_unserialize(actual,size));
            CHECK(save_hash() == flash);
        }
        buttons[5] = 0;
        av_enable = 3; state_context = 0;
        CHECK(p_serialize(actual,size));
        if (memcmp(expected,actual,size) != 0) {
            char path[4096]; join_path(path,sizeof(path),argv[2],"expected.state");
            FILE *file = fopen(path,"wb"); CHECK(file); CHECK(fwrite(expected,1,size,file)==size); fclose(file);
            join_path(path,sizeof(path),argv[2],"actual.state"); file = fopen(path,"wb"); CHECK(file);
            CHECK(fwrite(actual,1,size,file)==size); fclose(file);
            CHECK(memcmp(expected,actual,size) == 0);
        }
        printf("PASS checkpoint=%u exact video/audio/state replay and speculative rollback\n",target); fflush(stdout);
        state_context = 1; CHECK(p_unserialize(saved,size)); state_context = 0;
        buttons[0] = 0; position = target;
    }
    /* Keep several independently restorable frames, then walk history backwards. */
    unsigned char *history[8]; uint64_t pictures[8], samples[8];
    for (unsigned f=0; f<8; f++) {
        history[f] = (unsigned char *)malloc(size); CHECK(history[f] && p_serialize(history[f],size));
        buttons[0] = f & 1 ? 1 << RETRO_DEVICE_ID_JOYPAD_A : 0;
        p_run(); CHECK(messages == 0); pictures[f] = video_hash; samples[f] = audio_hash;
    }
    for (int f=7; f>=0; f--) {
        CHECK(p_unserialize(history[f],size));
        buttons[0] = f & 1 ? 1 << RETRO_DEVICE_ID_JOYPAD_A : 0;
        p_run(); CHECK(messages == 0 && video_hash == pictures[f] && audio_hash == samples[f]);
        free(history[f]);
    }
    puts("PASS: reverse traversal of independent rewind snapshots");
    /* RetroArch 1.22's single-instance sequence disables A/V on the committed
       frame and enables audio on the frame that will subsequently be rolled back. */
    for (unsigned f=0; f<12; f++) {
        buttons[5] = 0; av_enable = 0; p_run(); CHECK(messages == 0);
        state_context = 1; CHECK(p_serialize(saved,size)); uint64_t flash = save_hash();
        buttons[5] = 1 << RETRO_DEVICE_ID_JOYPAD_B;
        av_enable = 3; p_run(); CHECK(messages == 0 && flash == save_hash());
        CHECK(p_unserialize(saved,size)); CHECK(flash == save_hash());
    }
    av_enable = 3; state_context = 0; buttons[5] = 0;
    puts("PASS: audio-enabled speculative frames do not write flash saves");
    p_unload(); p_deinit(); close_library(lib); free(saved); free(expected); free(actual);
    puts("PASS: deterministic rewind and runahead"); return 0;
}
