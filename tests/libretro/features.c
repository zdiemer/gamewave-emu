/* Modern optional interfaces, including negotiation with older frontends. */
#define main legacy_smoke_main
#define environment legacy_environment
#include "smoke.c"
#undef environment
#undef main

static unsigned option_version = 2, legacy_options, v1_options, v2_options;
static bool extended_disks = true;
static struct retro_disk_control_ext_callback disk_ext;
static struct retro_memory_map memory_map;
static void *(*p_memory)(unsigned);
static size_t (*p_memory_size)(unsigned);
static void (*p_cheat)(unsigned, bool, const char *), (*p_cheat_reset)(void);

static bool RETRO_CALLCONV environment(unsigned cmd, void *data)
{
    switch (cmd) {
        case RETRO_ENVIRONMENT_SET_MEMORY_MAPS:
            memory_map = *(struct retro_memory_map *)data;
            if (memory_map.num_descriptors) {
                CHECK(memory_map.num_descriptors == 1);
                CHECK(memory_map.descriptors[0].flags == RETRO_MEMDESC_SAVE_RAM);
                CHECK(strcmp(memory_map.descriptors[0].addrspace,"SRAM") == 0);
            }
            return true;
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

static void write_le32(unsigned char *destination, uint32_t value)
{
    for (int i=0; i<4; i++) { destination[i] = (unsigned char)value; value >>= 8; }
}
static size_t make_sram(unsigned char *memory, size_t size)
{
    memset(memory,0,size); memcpy(memory,"GWSRAM1",8); memcpy(memory+16,"GWSAVE1",8);
    write_le32(memory+24,1); write_le32(memory+28,1); size_t position = 32;
    memory[position++] = 7; memcpy(memory+position,"Netplay",7); position += 7;
    memory[position++] = 6; memcpy(memory+position,"Scores",6); position += 6;
    write_le32(memory+position,4); position += 4;
    memory[position] = 42; write_le32(memory+8,(uint32_t)(position+4-16)); return position;
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
    LOAD(p_memory,"retro_get_memory_data"); LOAD(p_memory_size,"retro_get_memory_size");
    LOAD(p_cheat,"retro_cheat_set"); LOAD(p_cheat_reset,"retro_cheat_reset");
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
    CHECK(disk_ext.get_image_index() == 0);
    size_t ram_size = p_memory_size(RETRO_MEMORY_SAVE_RAM); CHECK(ram_size == 4u*1024u*1024u);
    unsigned char *ram = (unsigned char *)p_memory(RETRO_MEMORY_SAVE_RAM); CHECK(ram && !p_memory(RETRO_MEMORY_SYSTEM_RAM));
    CHECK(memory_map.descriptors[0].ptr == ram && memory_map.descriptors[0].len == ram_size);
    size_t offset = make_sram(ram,ram_size); p_run(); CHECK(messages == 0 && ram[offset] == 42);
    CHECK(p_memory(RETRO_MEMORY_SAVE_RAM) == ram);
    size_t state_size = p_serialize_size(); unsigned char *state = (unsigned char *)malloc(state_size); CHECK(state && p_serialize(state,state_size));
    ram[offset] = 99; p_run(); CHECK(ram[offset] == 99);
    CHECK(p_unserialize(state,state_size) && ram[offset] == 42 && p_memory(RETRO_MEMORY_SAVE_RAM) == ram); free(state);
    char cheat[128]; snprintf(cheat,sizeof(cheat),"sram:%X:8:2A",(unsigned)offset);
    p_cheat(0,true,cheat); ram[offset] = 99; p_run(); CHECK(ram[offset] == 42);
    p_cheat(0,false,NULL); ram[offset] = 77; p_run(); CHECK(ram[offset] == 77);
    p_cheat(0,true,cheat); p_cheat_reset(); ram[offset] = 88; p_run(); CHECK(ram[offset] == 88);
    ram[0] ^= 1; unsigned previous_messages = messages; p_run(); CHECK(messages == previous_messages+1 && ram[offset] == 88);
    p_cheat(1,true,"sram:FFFFFF:32:1"); CHECK(messages == previous_messages+2); p_run();
    p_unload(); CHECK(!p_memory(0) && !p_memory_size(0) && !memory_map.num_descriptors);
    CHECK(p_load(&game) && disk_ext.get_image_index() == 0); p_unload(); p_deinit();
    option_version = 1; extended_disks = false; p_environment(environment); p_init();
    CHECK(v1_options == 1 && !legacy_options && disk.set_image_index); p_deinit();
    option_version = 0; p_environment(environment); p_init(); CHECK(legacy_options == 1); p_deinit();
    close_library(lib);
    puts("PASS: extended disks, core options, frontend SRAM, memory maps and cheats"); return 0;
}
