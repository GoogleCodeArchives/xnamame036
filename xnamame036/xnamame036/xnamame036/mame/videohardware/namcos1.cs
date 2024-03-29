﻿#define NAMCOS1_DIRECT_DRAW

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace xnamame036.mame
{
    partial class namcos1
    {
        const int SPRITECOLORS = 2048;
        const int TILECOLORS = 1536;
        const int BACKGROUNDCOLOR = (SPRITECOLORS + 2 * TILECOLORS);

        /* support non use tilemap system draw routine */

        static _BytePtr get_gfx_pointer(Mame.GfxElement gfxelement, int c, int line)
        {
            return new _BytePtr(gfxelement.gfxdata, (c * gfxelement.height + line) * gfxelement.line_modulo);
        }
        /*
          video ram map
          0000-1fff : scroll playfield (0) : 64*64*2
          2000-3fff : scroll playfield (1) : 64*64*2
          4000-5fff : scroll playfield (2) : 64*64*2
          6000-6fff : scroll playfield (3) : 64*32*2
          7000-700f : ?
          7010-77ef : fixed playfield (4)  : 36*28*2
          77f0-77ff : ?
          7800-780f : ?
          7810-7fef : fixed playfield (5)  : 36*28*2
          7ff0-7fff : ?
        */
        static _BytePtr namcos1_videoram = new _BytePtr(1);
        /*
          paletteram map (s1ram  0x0000-0x7fff)
          0000-17ff : palette page0 : sprite
          2000-37ff : palette page1 : playfield
          4000-57ff : palette page2 : playfield (shadow)
          6000-7fff : work ram ?
        */
        static _BytePtr namcos1_paletteram = new _BytePtr(1);
        /*
          controlram map (s1ram 0x8000-0x9fff)
          0000-07ff : work ram
          0800-0fef : sprite ram	: 0x10 * 127
          0ff0-0fff : display control register
          1000-1fff : playfield control register
        */
        static _BytePtr namcos1_controlram = new _BytePtr(1);

        const int FG_OFFSET = 0x7000;

        const byte MAX_PLAYFIELDS = 6;
        const byte MAX_SPRITES = 127;

        struct playfield
        {
            public _BytePtr _base;
            public int scroll_x;
            public int scroll_y;
#if NAMCOS1_DIRECT_DRAW
            public int width;
            public int height;
#endif
            public Mame.tilemap tilemap;
            public int color;
        };

        static playfield[] playfields = new playfield[MAX_PLAYFIELDS];

#if NAMCOS1_DIRECT_DRAW
        static int namcos1_tilemap_need = 0;
        static int namcos1_tilemap_used;

        static _BytePtr char_state;
        const byte CHAR_BLANK = 0, CHAR_FULL = 1;
#endif

        /* playfields maskdata for tilemap */
        static _BytePtr[] mask_ptr;
        static _BytePtr mask_data;

        /* graphic object */
        static Mame.gfx_object_list objectlist;
        static Mame.gfx_object[] objects;

        /* palette dirty information */
        static bool[] sprite_palette_state = new bool[MAX_SPRITES + 1];
        static bool[] tilemap_palette_state = new bool[MAX_PLAYFIELDS];

        /* per game scroll adjustment */
        static int[] scrolloffsX = new int[4];
        static int[] scrolloffsY = new int[4];

        static int sprite_fixed_sx;
        static int sprite_fixed_sy;
        static int flipscreen;
        static int namcos1_videoram_r(int offset)
        {
            return namcos1_videoram[offset];
        }

        static void namcos1_videoram_w(int offset, int data)
        {
            if (namcos1_videoram[offset] != data)
            {
                namcos1_videoram[offset] = (byte)data;
#if NAMCOS1_DIRECT_DRAW
                if (namcos1_tilemap_used != 0)
                {
#endif
                    if (offset < FG_OFFSET)
                    {	/* background 0-3 */
                        int layer = offset / 0x2000;
                        int num = (offset &= 0x1fff) / 2;
                        Mame.tilemap_mark_tile_dirty(playfields[layer].tilemap, num % 64, num / 64);
                    }
                    else
                    {	/* foreground 4-5 */
                        int layer = (offset & 0x800) != 0 ? 5 : 4;
                        int num = ((offset & 0x7ff) - 0x10) / 2;
                        if (num >= 0 && num < 0x3f0)
                            Mame.tilemap_mark_tile_dirty(playfields[layer].tilemap, num % 36, num / 36);
                    }
#if NAMCOS1_DIRECT_DRAW
                }
#endif
            }
        }

        static int namcos1_paletteram_r(int offset)
        {
            return namcos1_paletteram[offset];
        }

        static void namcos1_paletteram_w(int offset, int data)
        {
            if (namcos1_paletteram[offset] != data)
            {
                namcos1_paletteram[offset] = (byte)data;
                if ((offset & 0x1fff) < 0x1800)
                {
                    if (offset < 0x2000)
                    {
                        sprite_palette_state[(offset & 0x7f0) / 16] = true;
                    }
                    else
                    {
                        int i, color;

                        color = (offset & 0x700) / 256;
                        for (i = 0; i < MAX_PLAYFIELDS; i++)
                        {
                            if (playfields[i].color == color)
                                tilemap_palette_state[i] = true;
                        }
                    }
                }
            }
        }
        static void namcos1_set_optimize(int optimize)
        {
#if NAMCOS1_DIRECT_DRAW
            namcos1_tilemap_need = optimize;
#endif
        }
        static void namcos1_spriteram_w(int offset, int data)
        {
            int[] sprite_sizemap = { 16, 8, 32, 4 };
            int num = offset / 0x10;
            Mame.gfx_object obj = objectlist.objects[num + MAX_PLAYFIELDS];
            _BytePtr _base = new _BytePtr(namcos1_controlram, 0x0800 + num * 0x10);
            int sx, sy;
            int resize_x = 0, resize_y = 0;

            switch (offset & 0x0f)
            {
                case 0x04:
                    /* bit.6-7 : x size (16/8/32/4) */
                    /* bit.5   : flipx */
                    /* bit.3-4 : x offset */
                    /* bit.0-2 : code.8-10 */
                    obj.width = sprite_sizemap[(data >> 6) & 3];
                    obj.flipx = ((data >> 5) & 1) ^ flipscreen;
                    obj.left = (data & 0x18) & (~(obj.width - 1));
                    obj.code = (_base[4] & 7) * 256 + _base[5];
                    resize_x = 1;
                    break;
                case 0x05:
                    /* bit.0-7 : code.0-7 */
                    obj.code = (_base[4] & 7) * 256 + _base[5];
                    break;
                case 0x06:
                    /* bit.1-7 : color */
                    /* bit.0   : x draw position.8 */
                    obj.color = data >> 1;
                    obj.transparency = obj.color == 0x7f ? Mame.TRANSPARENCY_PEN_TABLE : Mame.TRANSPARENCY_PEN;
                    goto case 0x07;
#if false
		if(object.color==0x7f && !(Machine.gamedrv.flags & GAME_REQUIRES_16BIT))
			usrintf_showmessage("This driver requires GAME_REQUIRES_16BIT flag");
#endif
                case 0x07:
                    /* bit.0-7 : x draw position.0-7 */
                    resize_x = 1;
                    break;
                case 0x08:
                    /* bit.5-7 : priority */
                    /* bit.3-4 : y offset */
                    /* bit.1-2 : y size (16/8/32/4) */
                    /* bit.0   : flipy */
                    obj.priority = (data >> 5) & 7;
                    obj.height = sprite_sizemap[(data >> 1) & 3];
                    obj.flipy = (data & 1) ^ flipscreen;
                    obj.top = (data & 0x18) & (~(obj.height - 1));
                    goto case 0x09;
                case 0x09:
                    /* bit.0-7 : y draw position */
                    resize_y = 1;
                    break;
                default:
                    return;
            }
            if (resize_x != 0)
            {
                /* sx */
                sx = (_base[6] & 1) * 256 + _base[7];
                sx += sprite_fixed_sx;

                if (flipscreen != 0) sx = 210 - sx - obj.width;

                if (sx > 480) sx -= 512;
                if (sx < -32) sx += 512;
                if (sx < -224) sx += 512;
                obj.sx = sx;
            }
            if (resize_y != 0)
            {
                /* sy */
                sy = sprite_fixed_sy - _base[9];

                if (flipscreen != 0) sy = 222 - sy;
                else sy = sy - obj.height;

                if (sy > 224) sy -= 256;
                if (sy < -32) sy += 256;
                obj.sy = sy;
            }
            obj.dirty_flag = Mame.GFXOBJ_DIRTY_ALL;
        }
        static void namcos1_displaycontrol_w(int offset, int data)
        {
            _BytePtr disp_reg = new _BytePtr(namcos1_controlram, 0xff0);
            int newflip;

            switch (offset)
            {
                case 0x02: /* ?? */
                    break;
                case 0x04: /* sprite offset X */
                case 0x05:
                    sprite_fixed_sx = disp_reg[4] * 256 + disp_reg[5] - 151;
                    if (sprite_fixed_sx > 480) sprite_fixed_sx -= 512;
                    if (sprite_fixed_sx < -32) sprite_fixed_sx += 512;
                    break;
                case 0x06: /* flip screen */
                    newflip = (disp_reg[6] & 1) ^ 0x01;
                    if (flipscreen != newflip)
                    {
                        namcos1_set_flipscreen(newflip);
                    }
                    break;
                case 0x07: /* sprite offset Y */
                    sprite_fixed_sy = 239 - disp_reg[7];
                    break;
                case 0x0a: /* ?? */
                    /* 00 : blazer,dspirit,quester */
                    /* 40 : others */
                    break;
                case 0x0e: /* ?? */
                /* 00 : blazer,dangseed,dspirit,pacmania,quester */
                /* 06 : others */
                case 0x0f: /* ?? */
                    /* 00 : dangseed,dspirit,pacmania */
                    /* f1 : blazer */
                    /* f8 : galaga88,quester */
                    /* e7 : others */
                    break;
            }
#if false
	{
		char buf[80];
		sprintf(buf,"%02x:%02x:%02x:%02x:%02x%02x,%02x,%02x,%02x:%02x:%02x:%02x:%02x:%02x:%02x:%02x",
		disp_reg[0],disp_reg[1],disp_reg[2],disp_reg[3],
		disp_reg[4],disp_reg[5],disp_reg[6],disp_reg[7],
		disp_reg[8],disp_reg[9],disp_reg[10],disp_reg[11],
		disp_reg[12],disp_reg[13],disp_reg[14],disp_reg[15]);
		usrintf_showmessage(buf);
	}
#endif
        }
        static void namcos1_set_flipscreen(int flip)
        {
            int i;

            int[] pos_x = { 0x0b0, 0x0b2, 0x0b3, 0x0b4 };
            int[] pos_y = { 0x108, 0x108, 0x108, 0x008 };
            int[] neg_x = { 0x1d0, 0x1d2, 0x1d3, 0x1d4 };
            int[] neg_y = { 0x1e8, 0x1e8, 0x1e8, 0x0e8 };

            flipscreen = flip;
            if (flip == 0)
            {
                for (i = 0; i < 4; i++)
                {
                    scrolloffsX[i] = pos_x[i];
                    scrolloffsY[i] = pos_y[i];
                }
            }
            else
            {
                for (i = 0; i < 4; i++)
                {
                    scrolloffsX[i] = neg_x[i];
                    scrolloffsY[i] = neg_y[i];
                }
            }
#if NAMCOS1_DIRECT_DRAW
            if (namcos1_tilemap_used != 0)
#endif
                Mame.tilemap_set_flip(Mame.ALL_TILEMAPS, flipscreen != 0 ? Mame.TILEMAP_FLIPX | Mame.TILEMAP_FLIPY : 0);
        }

        static void namcos1_videocontrol_w(int offset, int data)
        {
            namcos1_controlram[offset] = (byte)data;
            /* 0000-07ff work ram */
            if (offset <= 0x7ff)
                return;
            /* 0800-0fef sprite ram */
            if (offset <= 0x0fef)
            {
                namcos1_spriteram_w(offset & 0x7ff, data);
                return;
            }
            /* 0ff0-0fff display control ram */
            if (offset <= 0x0fff)
            {
                namcos1_displaycontrol_w(offset & 0x0f, data);
                return;
            }
            /* 1000-1fff control ram */
            namcos1_playfield_control_w(offset & 0xff, data);
        }
        static void namcos1_playfield_control_w(int offs, int data)
        {
            /* 0-15 : scrolling */
            if (offs < 16)
            {
                int whichone = offs / 4;
                int xy = offs & 2;
                if (xy == 0)
                { /* scroll x */
                    if ((offs & 1) != 0)
                        playfields[whichone].scroll_x = (playfields[whichone].scroll_x & 0xff00) | data;
                    else
                        playfields[whichone].scroll_x = (playfields[whichone].scroll_x & 0xff) | (data << 8);
                }
                else
                { /* scroll y */
                    if ((offs & 1) != 0)
                        playfields[whichone].scroll_y = (playfields[whichone].scroll_y & 0xff00) | data;
                    else
                        playfields[whichone].scroll_y = (playfields[whichone].scroll_y & 0xff) | (data << 8);
                }
            }
            /* 16-21 : priority */
            else if (offs < 22)
            {
                /* bit 0-2 priority */
                /* bit 3   disable	*/
                int whichone = offs - 16;
                objects[whichone].priority = data & 7;
                objects[whichone].visible = (data & 0xf8) != 0 ? 0 : 1;
#if NAMCOS1_DIRECT_DRAW
                if (namcos1_tilemap_used != 0)
#endif
                    playfields[whichone].tilemap.enable = objects[whichone].visible;
            }
            /* 22,23 unused */
            else if (offs < 24)
            {
            }
            /* 24-29 palette */
            else if (offs < 30)
            {
                int whichone = offs - 24;
                if (playfields[whichone].color != (data & 7))
                {
                    playfields[whichone].color = data & 7;
                    tilemap_palette_state[whichone] = true;
                }
            }
        }

        public static void namcos1_vh_convert_color_prom(byte[] palette, ushort[] colortable, _BytePtr color_prom)
        {
            for (int i = 0; i < Mame.Machine.drv.total_colors; i++)
            {
                palette[i * 3 + 0] = 0;
                palette[i * 3 + 1] = 0;
                palette[i * 3 + 2] = 0;
            }
        }
        static _BytePtr info_vram;
        static int info_color;

        static void background_get_info(int col, int row)
        {
            int tile_index = (row * 64 + col) * 2;
            int code = info_vram[tile_index + 1] + ((info_vram[tile_index] & 0x3f) << 8);
            Mame.SET_TILE_INFO(1, code, info_color);
            Mame.tile_info.mask_data = mask_ptr[code];
        }
#if NAMCOS1_DIRECT_DRAW
        static void draw_background(Mame.osd_bitmap bitmap, int layer)
        {
            _BytePtr vid = playfields[layer]._base;
            int width = playfields[layer].width;
            int height = playfields[layer].height;
            int color = objects[layer].color;
            int scrollx = playfields[layer].scroll_x;
            int scrolly = playfields[layer].scroll_y;
            int sx, sy;
            int offs_x, offs_y;
            int ox, xx;
            int max_x = Mame.Machine.drv.visible_area.max_x;
            int max_y = Mame.Machine.drv.visible_area.max_y;
            int code;

            scrollx -= scrolloffsX[layer];
            scrolly -= scrolloffsY[layer];

            if (flipscreen != 0)
            {
                scrollx = -scrollx;
                scrolly = -scrolly;
            }

            if (scrollx < 0) scrollx = width - (-scrollx) % width;
            else scrollx %= width;
            if (scrolly < 0) scrolly = height - (-scrolly) % height;
            else scrolly %= height;

            width /= 8;
            height /= 8;
            sx = (scrollx % 8);
            offs_x = width - (scrollx / 8);
            sy = (scrolly % 8);
            offs_y = height - (scrolly / 8);
            if (sx > 0)
            {
                sx -= 8;
                offs_x--;
            }
            if (sy > 0)
            {
                sy -= 8;
                offs_y--;
            }

            /* draw for visible area */
            offs_x *= 2;
            width *= 2;
            offs_y *= width;
            height = height * width;
            for (; sy <= max_y; offs_y += width, sy += 8)
            {
                offs_y %= height;
                for (ox = offs_x, xx = sx; xx <= max_x; ox += 2, xx += 8)
                {
                    ox %= width;
                    code = vid[offs_y + ox + 1] + ((vid[offs_y + ox] & 0x3f) << 8);
                    if (char_state[code] != CHAR_BLANK)
                    {
                        Mame.drawgfx(bitmap, Mame.Machine.gfx[1],
                                (uint)code,
                                (uint)color,
                                flipscreen != 0, flipscreen != 0,
                                flipscreen != 0 ? max_x - 7 - xx : xx,
                                flipscreen != 0 ? max_y - 7 - sy : sy,
                                Mame.Machine.drv.visible_area,
                                (char_state[code] == CHAR_FULL) ? Mame.TRANSPARENCY_NONE : Mame.TRANSPARENCY_PEN,
                                char_state[code]);
                    }
                }
            }
        }
        static void draw_foreground(Mame.osd_bitmap bitmap, int layer)
        {
            int offs;
            _BytePtr vid = playfields[layer]._base;
            int color = objects[layer].color;
            int max_x = Mame.Machine.drv.visible_area.max_x;
            int max_y = Mame.Machine.drv.visible_area.max_y;

            for (offs = 0; offs < 36 * 28 * 2; offs += 2)
            {
                int sx, sy, code;

                code = vid[offs + 1] + ((vid[offs + 0] & 0x3f) << 8);
                if (char_state[code] != CHAR_BLANK)
                {
                    sx = ((offs / 2) % 36) * 8;
                    sy = ((offs / 2) / 36) * 8;
                    if (flipscreen != 0)
                    {
                        sx = max_x - 7 - sx;
                        sy = max_y - 7 - sy;
                    }

                    Mame.drawgfx(bitmap, Mame.Machine.gfx[1],
                            (uint)code,
                            (uint)color,
                            flipscreen != 0, flipscreen != 0,
                            sx, sy,
                            Mame.Machine.drv.visible_area,
                            (char_state[code] == CHAR_FULL) ? Mame.TRANSPARENCY_NONE : Mame.TRANSPARENCY_PEN,
                            char_state[code]);
                }
            }
        }
#endif

        static void foreground_get_info(int col, int row)
        {
            int tile_index = (row * 36 + col) * 2;
            int code = info_vram[tile_index + 1] + ((info_vram[tile_index] & 0x3f) << 8);
            Mame.SET_TILE_INFO(1, code, info_color);
            Mame.tile_info.mask_data = mask_ptr[code];
        }
        static void ns1_draw_tilemap(Mame.osd_bitmap bitmap, Mame.gfx_object _object)
        {
            int layer = _object.code;
#if NAMCOS1_DIRECT_DRAW
            if (namcos1_tilemap_used != 0)
#endif
                Mame.tilemap_draw(bitmap, playfields[layer].tilemap, 0);
#if NAMCOS1_DIRECT_DRAW
            else
            {
                if (layer < 4)
                    draw_background(bitmap, layer);
                else
                    draw_foreground(bitmap, layer);
            }
#endif
        }

        public static int namcos1_vh_start()
        {
            Mame.gfx_object default_object;

#if NAMCOS1_DIRECT_DRAW
            /* tilemap used flag select */
            if (Mame.Machine.scrbitmap.depth == 16)
                /* tilemap system is not supported 16bit yet */
                namcos1_tilemap_used = 0;
            else
                /* selected by game option switch */
                namcos1_tilemap_used = namcos1_tilemap_need;
#endif

            /* set table for sprite color == 0x7f */
            for (int i = 0; i <= 15; i++)
                Mame.gfx_drawmode_table[i] = Mame.DRAWMODE_SHADOW;

            /* set static memory points */
            namcos1_paletteram = Mame.memory_region(Mame.REGION_USER2);
            namcos1_controlram = new _BytePtr(Mame.memory_region(Mame.REGION_USER2), 0x8000);

            /* allocate videoram */
            namcos1_videoram = new _BytePtr(0x8000);
            //memset(namcos1_videoram,0,0x8000);

            /* initialize object manager */
            default_object = new Mame.gfx_object();//memset(&default_object,0,sizeof(struct gfx_object));
            default_object.transparency = Mame.TRANSPARENCY_PEN;
            default_object.transparent_color = 15;
            default_object.gfx = Mame.Machine.gfx[2];
            objectlist = Mame.gfxobj_create(MAX_PLAYFIELDS + MAX_SPRITES, 8, default_object);

            objects = objectlist.objects;

            /* setup tilemap parameter to objects */
            for (int i = 0; i < MAX_PLAYFIELDS; i++)
            {
                /* set user draw handler */
                objects[i].special_handler = ns1_draw_tilemap;
                objects[i].gfx = null;
                objects[i].code = i;
                objects[i].visible = 0;
                objects[i].color = i;
            }

            /* initialize playfields */
            for (int i = 0; i < MAX_PLAYFIELDS; i++)
            {
#if NAMCOS1_DIRECT_DRAW
                if (namcos1_tilemap_used != 0)
                {
#endif
                    if (i < 4)
                    {
                        playfields[i]._base = new _BytePtr(namcos1_videoram, i << 13);
                        playfields[i].tilemap =
                            Mame.tilemap_create(background_get_info, Mame.TILEMAP_BITMASK
                                            , 8, 8
                                            , 64, i == 3 ? 32 : 64);
                    }
                    else
                    {
                        playfields[i]._base = new _BytePtr(namcos1_videoram, FG_OFFSET + 0x10 + ((i - 4) * 0x800));
                        playfields[i].tilemap =
                            Mame.tilemap_create(foreground_get_info, Mame.TILEMAP_BITMASK
                                            , 8, 8
                                            , 36, 28);
                    }
#if NAMCOS1_DIRECT_DRAW
                }
                else
                {
                    if (i < 4)
                    {
                        playfields[i]._base = new _BytePtr(namcos1_videoram, i << 13);
                        playfields[i].width = 64 * 8;
                        playfields[i].height = (i == 3) ? 32 * 8 : 64 * 8;
                    }
                    else
                    {
                        playfields[i]._base = new _BytePtr(namcos1_videoram, FG_OFFSET + 0x10 + ((i - 4) * 0x800));
                        playfields[i].width = 36 * 8;
                        playfields[i].height = 28 * 8;
                    }
                }
#endif
                playfields[i].scroll_x = 0;
                playfields[i].scroll_y = 0;
            }
            namcos1_set_flipscreen(0);

            /* initialize sprites and display controller */
            for (int i = 0; i < 0x7ef; i++)
                namcos1_spriteram_w(i, 0);
            for (int i = 0; i < 0xf; i++)
                namcos1_displaycontrol_w(i, 0);
            for (int i = 0; i < 0xff; i++)
                namcos1_playfield_control_w(i, 0);

#if NAMCOS1_DIRECT_DRAW
            if (namcos1_tilemap_used != 0)
            {
#endif
                /* build tilemap mask data from gfx data of mask */
                /* because this driver use ORIENTATION_ROTATE_90 */
                /* mask data can't made by ROM image             */
                {
                    Mame.GfxElement mask = Mame.Machine.gfx[0];
                    int total = (int)mask.total_elements;
                    int width = mask.width;
                    int height = mask.height;
                    int line, x, c;

                    mask_ptr = new _BytePtr[total];
                    
                    mask_data = new _BytePtr(total * 8);
                    
                    for (c = 0; c < total; c++)
                    {
                        _BytePtr src_mask = new _BytePtr(mask_data, c * 8);
                        for (line = 0; line < height; line++)
                        {
                            _BytePtr maskbm = get_gfx_pointer(mask, c, line);
                            src_mask[line] = 0;
                            for (x = 0; x < width; x++)
                            {
                                src_mask[line] |= (byte)(maskbm[x] << (7 - x));
                            }
                        }
                        mask_ptr[c] = src_mask;
                        if (mask.pen_usage != null)
                        {
                            switch (mask.pen_usage[c])
                            {
                                case 0x01: mask_ptr[c][0] = Mame.TILEMAP_BITMASK_TRANSPARENT; break; /* blank */
                                case 0x02: mask_ptr[c][0] = unchecked((byte)Mame.TILEMAP_BITMASK_OPAQUE); break; /* full */
                            }
                        }
                    }
                }

#if NAMCOS1_DIRECT_DRAW
            }
            else /* namcos1_tilemap_used */
            {

                /* build char mask status table */
                {
                    Mame.GfxElement mask = Mame.Machine.gfx[0];
                    Mame.GfxElement pens = Mame.Machine.gfx[1];
                    int total = (int)mask.total_elements;
                    int width = mask.width;
                    int height = mask.height;
                    int line, x, c;

                    char_state = new _BytePtr(total);

                    for (c = 0; c < total; c++)
                    {
                        byte ordata = 0;
                        byte anddata = 0xff;
                        for (line = 0; line < height; line++)
                        {
                            _BytePtr maskbm = get_gfx_pointer(mask, c, line);
                            for (x = 0; x < width; x++)
                            {
                                ordata |= maskbm[x];
                                anddata &= maskbm[x];
                            }
                        }
                        if (ordata == 0) char_state[c] = CHAR_BLANK;
                        else if (anddata != 0) char_state[c] = CHAR_FULL;
                        else
                        {
                            /* search non used pen */
                            byte[] penmap = new byte[256];
                            byte trans_pen;
                            //memset(penmap,0,256);
                            for (line = 0; line < height; line++)
                            {
                                _BytePtr pensbm = get_gfx_pointer(pens, c, line);
                                for (x = 0; x < width; x++)
                                    penmap[pensbm[x]] = 1;
                            }
                            for (trans_pen = 2; trans_pen < 256; trans_pen++)
                            {
                                if (penmap[trans_pen] == 0) break;
                            }
                            char_state[c] = trans_pen; /* transparency color */
                            /* fill transparency color */
                            for (line = 0; line < height; line++)
                            {
                                _BytePtr maskbm = get_gfx_pointer(mask, c, line);
                                _BytePtr pensbm = get_gfx_pointer(pens, c, line);
                                for (x = 0; x < width; x++)
                                {
                                    if (maskbm[x] == 0) pensbm[x] = trans_pen;
                                }
                            }
                        }
                    }
                }

            } /* namcos1_tilemap_used */
#endif

            for (int i = 0; i < TILECOLORS; i++)
            {
                Mame.palette_shadow_table[Mame.Machine.pens[i + SPRITECOLORS]]= Mame.Machine.pens[i + SPRITECOLORS + TILECOLORS];
            }

            return 0;
        }

        public static void namcos1_vh_stop()
        {
            namcos1_videoram = null;

#if NAMCOS1_DIRECT_DRAW
            if (namcos1_tilemap_used != 0)
            {
#endif
                mask_ptr = null;
                mask_data = null;
#if NAMCOS1_DIRECT_DRAW
            }
            else
                char_state = null;
#endif
        }



        static void update_playfield(int layer)
        {
            Mame.tilemap tilemap = playfields[layer].tilemap;

            /* for background , set scroll position */
            if (layer < 4)
            {
                int scrollx = -playfields[layer].scroll_x + scrolloffsX[layer];
                int scrolly = -playfields[layer].scroll_y + scrolloffsY[layer];
                if (flipscreen != 0)
                {
                    scrollx = -scrollx;
                    scrolly = -scrolly;
                }
                /* set scroll */
                Mame.tilemap_set_scrollx(tilemap, 0, scrollx);
                Mame.tilemap_set_scrolly(tilemap, 0, scrolly);
            }
            info_vram = playfields[layer]._base;
            info_color = objects[layer].color;
            Mame.tilemap_update(tilemap);
        }
        static void namcos1_palette_refresh(int start, int offset, int num)
        {
            int color;

            offset = (offset / 0x800) * 0x2000 + (offset & 0x7ff);

            for (color = start; color < start + num; color++)
            {
                byte r, g, b;
                r = namcos1_paletteram[offset];
                g = namcos1_paletteram[offset + 0x0800];
                b = namcos1_paletteram[offset + 0x1000];
                Mame.palette_change_color(color, r, g, b);

                if (offset >= 0x2000)
                {
                    r = namcos1_paletteram[offset + 0x2000];
                    g = namcos1_paletteram[offset + 0x2800];
                    b = namcos1_paletteram[offset + 0x3000];
                    Mame.palette_change_color(color + TILECOLORS, r, g, b);
                }
                offset++;
            }
        }

        public static void namcos1_vh_screenrefresh(Mame.osd_bitmap bitmap, int full_refresh)
        {
            Mame.gfx_object _object;
            ushort[] palette_map = new ushort[MAX_SPRITES + 1];
            _BytePtr remapped;

            /* update all tilemaps */
#if NAMCOS1_DIRECT_DRAW
            if (namcos1_tilemap_used != 0)
            {
#endif
                for (int i = 0; i < MAX_PLAYFIELDS; i++)
                    update_playfield(i);
#if NAMCOS1_DIRECT_DRAW
            }
#endif
            /* object list (sprite) update */
            Mame.gfxobj_update();
            /* palette resource marking */
            Mame.palette_init_used_colors();
            //memset(palette_map, 0, sizeof(palette_map));
            for (_object = objectlist.first_object; _object != null; _object = _object.next)
            {
                if (_object.visible != 0)
                {
                    int color = _object.color;
                    if (_object.gfx != null)
                    {	/* sprite object */
                        if (sprite_palette_state[color])
                        {
                            if (color != 0x7f) namcos1_palette_refresh(16 * color, 16 * color, 15);
                            sprite_palette_state[color] = false;
                        }

                        palette_map[color] |= (ushort)Mame.Machine.gfx[2].pen_usage[_object.code];
                    }
                    else
                    {	/* playfield object */
                        if (tilemap_palette_state[color])
                        {
                            namcos1_palette_refresh(128 * 16 + 256 * color, 128 * 16 + 256 * playfields[color].color, 256);
#if NAMCOS1_DIRECT_DRAW
                            if (namcos1_tilemap_used == 0)
                            {
                                /* mark used flag */
                                for (int i = 0; i < 256; i++)
                                    Mame.palette_used_colors[i + color * 256 + 128 * 16] = Mame.PALETTE_COLOR_VISIBLE;
                            }
#endif
                            tilemap_palette_state[color] = false;
                        }
                    }
                }
            }

            for (int i = 0; i < MAX_SPRITES; i++)
            {
                int usage = palette_map[i], j;
                if (usage != 0)
                {
                    for (j = 0; j < 15; j++)
                        if ((usage & (1 << j)) != 0)
                            Mame.palette_used_colors[i * 16 + j] |= Mame.PALETTE_COLOR_VISIBLE;
                }
            }
            /* background color */
            Mame.palette_used_colors[BACKGROUNDCOLOR] |= Mame.PALETTE_COLOR_VISIBLE;

            if ((remapped = Mame.palette_recalc()) != null)
            {
#if NAMCOS1_DIRECT_DRAW
                if (namcos1_tilemap_used != 0)
#endif
                    for (int i = 0; i < MAX_PLAYFIELDS; i++)
                    {
                        _BytePtr remapped_layer = new _BytePtr(remapped, 128 * 16 + 256 * i);
                        for (int j = 0; j < 256; j++)
                        {
                            if (remapped_layer[j] != 0)
                            {
                                Mame.tilemap_mark_all_pixels_dirty(playfields[i].tilemap);
                                break;
                            }
                        }
                    }
            }

#if NAMCOS1_DIRECT_DRAW
            if (namcos1_tilemap_used != 0)
#endif
                Mame.tilemap_render(Mame.ALL_TILEMAPS);
            /* background color */
            Mame.fillbitmap(bitmap, Mame.Machine.pens[BACKGROUNDCOLOR], Mame.Machine.drv.visible_area);
            /* draw objects (tilemaps and sprites) */
            Mame.gfxobj_draw(objectlist);
        }

    }
}
