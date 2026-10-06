/* Modern optional interfaces, including negotiation with older frontends. */
#define main legacy_smoke_main
#define environment legacy_environment
#include "smoke.c"
#undef environment
#undef main

static unsigned option_version = 2, legacy_options, v1_options, v2_options;
static bool extended_disks = true;
static struct retro_disk_control_ext_callback disk_ext;

static bool RETRO_CALLCONV environment(unsigned cmd, void *data)
{
    switch (cmd) {
        case RETRO_ENVIRONMENT_GET_CORE_OPTIONS_VERSION:
            *(unsigned *)data = option_version; return option_version != 0;
        case RETRO_ENVIRONMENT_SET_CORE_OPTIONS_V2: {
            const struct retro_core_options_v2 *options = (const struct retro_core_options_v2 *)data;
            CHECK(options->categories && options->definitions);
            CHECK(strcmp(options->categories[0].key,"video") == 0);
            CHECK(strcmp(options->categories[1].key,"input") == 0 && !options->categories[2].key);
            CHECK(strcmp(options->definitions[0].key,"gamewave_deinterlace") == 0);
            CHECK(strcmp(options->definitions[0].default_value,"blend") == 0);
            CHECK(strcmp(options->definitions[0].values[1].value,"off") == 0);
            CHECK(!options->definitions[0].values[2].value);
            CHECK(strcmp(options->definitions[1].values[5].value,"6") == 0);
            CHECK(!options->definitions[1].values[6].value && !options->definitions[2].key);
            v2_options++; return false; /* Valid registration without categories. */
        }
        case RETRO_ENVIRONMENT_SET_CORE_OPTIONS: {
            const struct retro_core_option_definition *options = (const struct retro_core_option_definition *)data;
            CHECK(strcmp(options[0].key,"gamewave_deinterlace") == 0);
            CHECK(strcmp(options[1].default_value,"1") == 0 && !options[2].key);
            v1_options++; return true;
        }
        case RETRO_ENVIRONMENT_SET_VARIABLES: legacy_options++; break;
        case RETRO_ENVIRONMENT_GET_DISK_CONTROL_INTERFACE_VERSION:
            *(unsigned *)data = extended_disks ? 1 : 0; return true;
        case RETRO_ENVIRONMENT_SET_DISK_CONTROL_EXT_INTERFACE:
            disk_ext = *(struct retro_disk_control_ext_callback *)data;
            CHECK(disk_ext.set_initial_image && disk_ext.get_image_path && disk_ext.get_image_label);
            return true;
        default: break;
    }
    return legacy_environment(cmd,data);
}

int main(int argc, char **argv)
{
    CHECK(argc == 3);
    char content[4096], tray[4096], playlist[4096], tray_info[4096], text[4096];
    join_path(content,sizeof(content),argv[2],"disc"); join_path(tray,sizeof(tray),argv[2],"tray");
    join_path(playlist,sizeof(playlist),argv[2],"discs.m3u"); join_path(tray_info,sizeof(tray_info),tray,"gamewave.diz");
    join_path(save_directory,sizeof(save_directory),argv[2],"saves");
    mkdir_one(argv[2]); fixture(content,false); fixture(tray,true);
    FILE *file = fopen(playlist,"wb"); CHECK(file);
    fputs("#EXTINF:0,First disc\ndisc/gamewave.diz\n#LABEL:Second disc\ntray/gamewave.diz\n",file); fclose(file);
    library_t lib = open_library(argv[1]); CHECK(lib); load_api(lib);
    p_environment(environment); CHECK(v2_options == 1 && !v1_options && !legacy_options);
    p_video(video); p_audio_batch(audio_batch); p_poll(poll); p_input(input); p_init();
    CHECK(!disk_ext.set_initial_image(1,NULL)); CHECK(disk_ext.set_initial_image(1,tray_info));
    struct retro_game_info game = {playlist,NULL,0,NULL}; CHECK(p_load(&game));
    CHECK(disk_ext.get_image_index() == 1 && disk_ext.get_num_images() == 2);
    CHECK(disk_ext.get_image_path(1,text,sizeof(text)) && strstr(text,"gamewave.diz"));
    CHECK(disk_ext.get_image_label(1,text,sizeof(text)) && strcmp(text,"Second disc") == 0);
    CHECK(!disk_ext.get_image_label(2,text,sizeof(text)) && !text[0]);
    text[0] = 'x'; CHECK(!disk_ext.get_image_label(0,text,1) && !text[0]);
    CHECK(!disk_ext.get_image_path(0,NULL,0)); CHECK(!disk_ext.set_initial_image(0,playlist));
    CHECK(disk_ext.set_eject_state(true) && disk_ext.add_image_index());
    CHECK(!disk_ext.get_image_path(2,text,sizeof(text)));
    struct retro_game_info replacement = {tray_info,NULL,0,NULL};
    CHECK(disk_ext.replace_image_index(2,&replacement));
    CHECK(disk_ext.get_image_label(2,text,sizeof(text)) && strcmp(text,"tray") == 0);
    CHECK(disk_ext.replace_image_index(2,NULL) && disk_ext.get_num_images() == 2);
    p_unload(); CHECK(disk_ext.set_initial_image(1,playlist)); CHECK(p_load(&game));
    CHECK(disk_ext.get_image_index() == 0); p_run(); CHECK(messages == 0); p_unload();
    CHECK(p_load(&game) && disk_ext.get_image_index() == 0); p_unload(); p_deinit();
    option_version = 1; extended_disks = false; p_environment(environment); p_init();
    CHECK(v1_options == 1 && !legacy_options && disk.set_image_index); p_deinit();
    option_version = 0; p_environment(environment); p_init(); CHECK(legacy_options == 1); p_deinit();
    close_library(lib);
    puts("PASS: extended disks and core options v2/v1/legacy negotiation"); return 0;
}
