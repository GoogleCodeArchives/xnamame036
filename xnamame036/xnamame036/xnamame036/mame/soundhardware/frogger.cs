﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace xnamame036.mame.drivers
{
    partial class driver_frogger
    {


        /* The timer clock which feeds the upper 4 bits of    					*/
        /* AY-3-8910 port A is based on the same clock        					*/
        /* feeding the sound CPU Z80.  It is a divide by      					*/
        /* 5120, formed by a standard divide by 512,        					*/
        /* followed by a divide by 10 using a 4 bit           					*/
        /* bi-quinary count sequence. (See LS90 data sheet    					*/
        /* for an example).                                   					*/
        /*																		*/
        /* Bit 4 comes from the output of the divide by 1024  					*/
        /*       0, 1, 0, 1, 0, 1, 0, 1, 0, 1									*/
        /* Bit 3 comes from the QC output of the LS90 producing a sequence of	*/
        /* 		 0, 0, 1, 1, 0, 0, 1, 1, 1, 0									*/
        /* Bit 6 comes from the QD output of the LS90 producing a sequence of	*/
        /*		 0, 0, 0, 0, 1, 0, 0, 0, 0, 1									*/
        /* Bit 7 comes from the QA output of the LS90 producing a sequence of	*/
        /*		 0, 0, 0, 0, 0, 1, 1, 1, 1, 1			 						*/

        static int[] frogger_timer = { 0x00, 0x10, 0x08, 0x18, 0x40, 0x90, 0x88, 0x98, 0x88, 0xd0 };

        /* need to protect from totalcycles overflow */
        static int last_totalcycles = 0;

        /* number of Z80 clock cycles to count */
        static int clock;
        static int frogger_portB_r(int offset)
        {

            int current_totalcycles;

            current_totalcycles = Mame.cpu_gettotalcycles();
            clock = (clock + (current_totalcycles - last_totalcycles)) % 5120;

            last_totalcycles = current_totalcycles;

            return frogger_timer[clock / 512];
        }
        static int last_frogger_sh_irqtrigger_w;
        static void frogger_sh_irqtrigger_w(int offset, int data)
        {


            if (last_frogger_sh_irqtrigger_w == 0 && (data & 0x08) != 0)
            {
                /* setting bit 3 low then high triggers IRQ on the sound CPU */
                Mame.cpu_cause_interrupt(1, 0xff);
            }

            last_frogger_sh_irqtrigger_w = data & 0x08;
        }
        static int last_frogger2_sh_irqtrigger_w;
        static void frogger2_sh_irqtrigger_w(int offset, int data)
        {


            if (last_frogger2_sh_irqtrigger_w == 0 && (data & 0x01) != 0)
            {
                /* setting bit 0 low then high triggers IRQ on the sound CPU */
                Mame.cpu_cause_interrupt(1, 0xff);
            }

            last_frogger2_sh_irqtrigger_w = data & 0x01;
        }
    }
}
