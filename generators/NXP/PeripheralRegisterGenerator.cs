using BSPEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nxp_bsp_generator
{
    static class PeripheralRegisterGenerator
    {
      
        class Peripheral
        {
            public string Name;
            public string Type;
            public ulong Address;

            public override string ToString()
            {
                return Name;
            }
        }

        class TypePreference
        {
            public string FileName;
            public string Type;
            public bool FirstOccurence;
        }

        class Family
        {
            public string Name;
            public List<Peripheral> SetBaseAddresses;// All the sets that need to be addressed and types to be found
            public Dictionary<string, int> Defines;// Manually added defines that help parse the files
            public List<string> HeadersToIgnore;// Files that are meant for other subfamilies or contain no structs
            public List<string> AdditionalStructDependencies;// Add all the types that have unknown sizes here to be parsed as well
            public Dictionary<string, TypePreference> TypePreferences;//Helps parsing if same type is declared twice in the same file 
        }

        private static Dictionary<string, ulong> STANDARD_TYPE_SIZES = new Dictionary<string, ulong>() { { "uint32_t", 32 }, { "uint16_t", 16 }, { "uint8_t", 8 } };

        public static void CleanPDFConvertedToText(string filePath)
        {
            List<string> lines = new List<string>(File.ReadAllLines(filePath));
            int old_line_count = lines.Count;

            lines.RemoveAll(x => { return (x.Trim() == ""); });// Remove all empty lines

            if (lines.Count != old_line_count)
            {
                Console.WriteLine("Cleant text converted from pdf at " + filePath);
                File.WriteAllLines(filePath, lines);
            }
        }

        public static void CleanConvertedUserManuals(string sortedDataDir)
        {
            foreach(var file in new DirectoryInfo(sortedDataDir).GetFiles("*.um", SearchOption.AllDirectories))
            {
                CleanPDFConvertedToText(file.FullName);
            }
        }
        public static Dictionary<string, HardwareRegisterSet[]> GenerateFamilyPeripheralRegistersAdd(string familyDirectory)
        {
            
            // Create the hardware register sets for each subfamily
            Dictionary<string, HardwareRegisterSet[]> peripherals = new Dictionary<string, HardwareRegisterSet[]>();
            // Create a hardware register set for each base address with the correctly calculated addresses
            List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
            HardwareRegisterSet set = new HardwareRegisterSet();
            set.UserFriendlyName = "ASSS1";
             
            sets.Add(set);
            set.UserFriendlyName = "ASSS2";
            sets.Add(set);
            peripherals.Add("LPC99XX1", sets.ToArray());
            peripherals.Add("LPC99XX2", sets.ToArray());
            peripherals.Add("LPC99XX3", sets.ToArray());

            return peripherals;

        }
        public static Dictionary<string, HardwareRegisterSet[]> GenerateFamilyPeripheralRegisters(string familyDirectory)
        {

    
            
            string family_name = Path.GetFileName(familyDirectory).Substring("lpc".Length);
            string headers_dir = familyDirectory + "\\lpc_chip\\chip_" + family_name;

            // Find the peripheral headers directory
            string chip_header = Path.Combine(headers_dir, "chip.h");
            if(!File.Exists(chip_header))
            {
                chip_header = familyDirectory + "\\Core\\DeviceSupport\\NXP\\lpc" + family_name + "\\LPC" + family_name + ".h";
                if (!File.Exists(chip_header))
                    throw new Exception("Peripheral headers not found!");
                headers_dir = Path.GetDirectoryName(chip_header);
            }

            // Create the subfamilies based on manually written csv helper file (transcribed from chip.h headers instead of parsing them)
            List<Family> subfamilies = new List<Family>();
            foreach(var csv_file in new DirectoryInfo(familyDirectory).GetFiles("*.csv"))
            {
                subfamilies.Add(ProcessRegisterSetAddresses(csv_file.FullName));
            }

            // Create the hardware register sets for each subfamily
            Dictionary<string, HardwareRegisterSet[]> peripherals = new Dictionary<string, HardwareRegisterSet[]>();
            foreach(var subfamily in subfamilies)
            {
                Regex indexed_name = new Regex(@"^(.*?)_([0-9]+)(_([0-9]+))?$");

                //if (subfamily.Name != "LPC15XX")
                //    continue;

                //List<string> skip = new List<string>(new string[] { "LPC12XX", "LPC110X", "LPC1125", "LPC11AXX", "LPC11CXX", "LPC11EXX", "LPC11UXX", "LPC11XXLV", "LPC1347", "LPC15XX", "LPC1343"/*, "LPC175X_6X", "LPC8XX", "LPC177X_8X", "LPC12XX", "LPC40XX"*/ });// 12xx have other issues as well
                //if (skip.Contains(subfamily.Name))
                //    continue;

                // Parse all the peripheral headers of the subfamily and create the hardware register sets
                Dictionary<string, string> nested_types;
                Dictionary<string, HardwareRegisterSet> registerset_types = ProcessRegisterSetTypes(headers_dir, subfamily,out nested_types);
                //Just for visual checking of the parsing
                //List<string> test_lines = new List<string>();
                //test_lines.Add("------------ " + subfamily.Name);
                //foreach(HardwareRegisterSet parsed_set in registerset_types.Values)
                //{
                //    test_lines.Add("--- " + parsed_set.UserFriendlyName);
                //    foreach(HardwareRegister parsed_reg in parsed_set.Registers)
                //    {
                //        if (!parsed_reg.Name.StartsWith("DATA_") && !parsed_reg.Name.StartsWith("B_") && !parsed_reg.Name.StartsWith("W_"))
                //            test_lines.Add("------ " + parsed_reg.Name);
                //    }
                //}
                //test_lines.Add("------------");
                //File.AppendAllLines("E:\\KET\\Temp\\nxp_test_parsing.txt", test_lines.ToArray());

                // Parse the text versions of the user manuals for the subregisters
                Dictionary<string, List<HardwareSubRegister>> subregisters = ProcessSubregisters(familyDirectory, subfamily);

                List<string> used_types = new List<string>();// For verification only: track which types are used
                List<string> used_subregisters = new List<string>();// For verification only: track which subregister lists of registers are matched

                // Rerouting subregisters for certain repeating name registers
                Dictionary<string, string> dict_setname_reg_to_reg = new Dictionary<string, string>();

                // Dictionary matching register names in header files to their names in documentation
                Dictionary<string, string> dict_set_reg_to_reg = new Dictionary<string, string>();// key is peripheral type+reg to avoid confusing registers

                // Dictionary matching the register set names in header files to their names in documentation
                Dictionary<string, string> dict_set_to_set = new Dictionary<string, string>();

                // List of registers that should reuse a nonindexed registers subregisters for indexed registers
                List<string> list_setname_reg_indexed = new List<string>();

                // List of registers that (faultily) do not exist in headers
                List<string> nonexisting_regs = new List<string>();

                #region LPC8XX
                if(subfamily.Name == "LPC8XX")
                {
                    nonexisting_regs.AddRange(new string[] {
                        //I2C
                        "TIMEOUT",
                        "CLKDIV",
                        "MSTCTL",
                        "MSTTIME",
                        "MSTDAT",
                        "SLVCTL",
                        "SLVDAT",
                        "SLVADR0",
                        "SLVADR1",
                        "SLVADR2",
                        "SLVADR3",
                        "SLVQUAL0",
                        "MONRXDAT",
                        "CFG{2}",
                        "STAT{2}",
                        "INTENSET{2}",
                        "INTENCLR{2}",
                        "INTSTAT{2}",
                        //NVIC
                        "ISER0",
                        "ICER0",
                        "ISPR0",
                        "ICPR0",
                        "IABR0",
                        "IPR0",
                        "IPR1",
                        "IPR2",
                        "IPR3",
                        "IPR6",
                        "IPR7",
                        "STIR",
                        //PMU
                        "SCR",
                        //SysTick
                        "SYST_CSR",
                        "SYST_RVR",
                        "SYST_CVR",
                        "SYST_CALIB",
                    });

                    dict_set_reg_to_reg.Add("LPC_CRC_T+WRDATA32", "WR_DATA");
                    for (int i = 0; i <= 17; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_0_" + i.ToString(), "B" + i.ToString());
                    for (int i = 0; i <= 17; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_0_" + i.ToString(), "W" + i.ToString());
                    for (int i = 0; i <= 3; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_INTVAL", "INTVAL" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_TIMER", "TIMER" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_CTRL", "CTRL" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_STAT", "STAT" + i.ToString());
                    }
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CTRL_U", "CTRL");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+COUNT_U", "COUNT");
                    for (int i = 0; i <= 5; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_SET_" + (i + 1).ToString(), "OUT" + i.ToString() + "_SET");
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_CLR_" + (i + 1).ToString(), "OUT" + i.ToString() + "_CLR");
                    }
                    for(int i=0;i<=15;i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_SCT_T+EVENT_STATE_" + (i + 1).ToString(), "EV" + i.ToString() + "_STATE");
                        dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_" + (i+1).ToString(), "MATCH" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_" + (i + 1).ToString(), "MATCHREL" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_" + (i + 1).ToString(), "CAP" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_" + (i + 1).ToString(), "CAPCTRL" + i.ToString());
                    }
                    for(int i=0;i<=5;i++)
                        dict_set_reg_to_reg.Add("LPC_SCT_T+EVENT_CTRL_" + (i + 1).ToString(), "EV" + i.ToString() + "_CTRL");
                    dict_set_reg_to_reg.Add("LPC_SPI_T+TXCTRL", "TXCTL");
                    dict_set_reg_to_reg.Add("LPC_USART_T+RXDATA_STAT", "RXDATSTAT");
                    dict_set_reg_to_reg.Add("LPC_USART_T+RXDATA", "RXDAT");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TXDATA", "TXDAT");

                    dict_setname_reg_to_reg.Add("USART0+CTRL", "CTL");
                    dict_setname_reg_to_reg.Add("USART1+CTRL", "CTL");
                    dict_setname_reg_to_reg.Add("USART2+CTRL", "CTL");

                    dict_setname_reg_to_reg.Add("WKT+CTRL", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("CMP+CTRL", "CTRL{3}");
                    dict_setname_reg_to_reg.Add("WKT+COUNT", "COUNT{2}");
                    dict_setname_reg_to_reg.Add("SPI0+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("SPI1+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("SPI0+CFG", "CFG{3}");
                    dict_setname_reg_to_reg.Add("SPI1+CFG", "CFG{3}");
                    dict_setname_reg_to_reg.Add("SPI0+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("SPI1+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("SPI0+INTENSET", "INTENSET{3}");
                    dict_setname_reg_to_reg.Add("SPI1+INTENSET", "INTENSET{3}");
                    dict_setname_reg_to_reg.Add("SPI0+INTENCLR", "INTENCLR{3}");
                    dict_setname_reg_to_reg.Add("SPI1+INTENCLR", "INTENCLR{3}");
                    dict_setname_reg_to_reg.Add("SPI0+RXDAT", "RXDAT{2}");
                    dict_setname_reg_to_reg.Add("SPI1+RXDAT", "RXDAT{2}");
                    dict_setname_reg_to_reg.Add("SPI0+TXDAT", "TXDAT{2}");
                    dict_setname_reg_to_reg.Add("SPI1+TXDAT", "TXDAT{2}");
                }
                #endregion
                #region LPC11XX
                #region LPC110X
                else if (subfamily.Name == "LPC110X")
                {
                    nonexisting_regs.AddRange(new string[] {
                            "IOCON_RESET_PIO0_0",
                            "IOCON_PIO0_1",
                            "IOCON_PIO0_6",
                            "IOCON_PIO0_8",
                            "IOCON_PIO0_9",
                            "IOCON_SWCLK_PIO0_10",
                            "IOCON_R_PIO0_11",
                            "IOCON_R_PIO1_0",
                            "IOCON_R_PIO1_1",
                            "IOCON_R_PIO1_2",
                            "IOCON_SWDIO_PIO1_3",
                            "IOCON_PIO1_6",
                            "IOCON_PIO1_7",
                            "IOCON_SCK_LOC",
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB"
                        });

                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL", "CLKOUTCLKSEL");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTDIV", "CLKOUTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_FMC_T+FLASHTIM", "FLASHCFG");
                    dict_set_reg_to_reg.Add("LPC_GPIO_T+RIS", "IRS");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "U0TER");

                    dict_set_to_set.Add("TIMER16_0", "TMR16B0");
                    dict_set_to_set.Add("TIMER16_1", "TMR16B1");
                    dict_set_to_set.Add("TIMER32_0", "TMR32B0");
                    dict_set_to_set.Add("TIMER32_1", "TMR32B1");
                    dict_set_to_set.Add("GPIO_PORT0", "GPIO0");
                    dict_set_to_set.Add("GPIO_PORT1", "GPIO1");
                    dict_set_to_set.Add("ADC", "AD0");
                    dict_set_to_set.Add("USART", "U0");
                    dict_set_to_set.Add("WWDT", "WD");

                    dict_set_to_set.Add("SSP0", "SSP0");

                    dict_setname_reg_to_reg.Add("TIMER32_1+CTCR", "TMR32B1TCR{2}");
                }
                #endregion
                #region LPC1125
                else if (subfamily.Name == "LPC1125")
                {
                    nonexisting_regs.AddRange(new string[] {
                            "UART1CLKDIV",
                            "RTCCLKDIV",
                            "IOCONFIGCLKDIV0",
                            "IOCONFIGCLKDIV1",
                            "IOCONFIGCLKDIV2",
                            "IOCONFIGCLKDIV3",
                            "IOCONFIGCLKDIV4",
                            "IOCONFIGCLKDIV5",
                            "IOCONFIGCLKDIV6",
                            "AHBPRIO",
                            "STARTAPRP1",
                            "STARTRSRP1CLR",
                            "STARTSRP1",
                            "DMACR",
                            "DMA_STATUS",
                            "DMA_CFG",
                            "ATL_CTRL_BASE_PTR",
                            "DMA_WAITONREQ_STATUS",
                            "CHNL_SW_REQUEST",
                            "CHNL_USEBURST_SET",
                            "CHNL_USEBURST_CLR",
                            "CHNL_REQ_MASK_SET",
                            "CHNL_REQ_MASK_CLR",
                            "CHNL_ENABLE_SET",
                            "CHNL_ENABLE_CLR",
                            "CHNL_PRI_ALT_SET",
                            "CHNL_PRI_ALT_CLR",
                            "CHNL_PRIORITY_SET",
                            "CHNL_PRIORITY_CLR",
                            "ERR_CLR",
                            "CHNL_IRQ_STATUS",
                            "IRQ_ERR_ENABLE",
                            "CHNL_IRQ_ENABLE",
                            "GDR",
                            "CMP",
                            "VLAD",
                            "CLKSEL",
                            "MR",
                            "LR",
                            "CR",
                            "ICSC",
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB",
                            "SYSCFG",
                            "IOCON",
                            "IOCON{2}",
                            "PIO0_19",
                            "PIO0_20",
                            "PIO0_21",
                            "PIO0_22",
                            "PIO0_23",
                            "PIO0_24",
                            "SWDIO_PIO0_25",
                            "SWCLK_PIO0_26",
                            "PIO0_27",
                            "PIO2_12",
                            "PIO2_13",
                            "PIO2_14",
                            "PIO2_15",
                            "PIO0_28",
                            "PIO0_29",
                            "PIO0_0",
                            "PIO0_1",
                            "PIO0_2",
                            "PIO0_3",
                            "PIO0_4",
                            "PIO0_5",
                            "PIO0_6",
                            "PIO0_7",
                            "PIO0_8",
                            "PIO0_9",
                            "PIO2_0",
                            "PIO2_1",
                            "PIO2_2",
                            "PIO2_3",
                            "PIO2_4",
                            "PIO2_5",
                            "PIO2_6",
                            "PIO2_7",
                            "PIO0_10",
                            "PIO0_11",
                            "PIO0_12",
                            "RESET_PIO0_13",
                            "PIO0_14",
                            "PIO0_15",
                            "PIO0_16",
                            "PIO0_17",
                            "PIO0_18",
                            "R_PIO0_30",
                            "R_PIO0_31",
                            "R_PIO1_0",
                            "R_PIO1_1",
                            "PIO1_2",
                            "PIO1_3",
                            "PIO1_4",
                            "PIO1_5",
                            "PIO1_6",
                            "PIO2_8",
                            "PIO2_9",
                            "PIO2_10",
                            "PIO2_11",
                            "MASK",
                            "PIN",
                            "OUT",
                            "SET",
                            "CLR",
                            "NOT",
                            //DMA,
                            "CTRL_BASE_PTR",
                            //RTC
                            "DR{2}",
                            "RIS{3}",
                            "MIS{3}",
                            "ICR{3}"
                        });

                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+SYSRSTSTAT", "SYSRESSTAT");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+SSP0CLKDIV", "SSPCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+SSP2CLKDIV", "SSPCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL", "CLKOUTCLKSEL");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UART0CLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+NMISRC", "INTNMI");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_FMC_T+FLASHTIM", "FLASHCFG");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");

                    dict_setname_reg_to_reg.Add("SSP0+RIS", "RIS{2}");
                    dict_setname_reg_to_reg.Add("SSP0+MIS", "MIS{2}");
                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+RIS", "RIS{2}");
                    dict_setname_reg_to_reg.Add("SSP1+MIS", "MIS{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_2", "CR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_2", "CR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_3", "CR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_3", "CR3{2}");
                    dict_setname_reg_to_reg.Add("ADC+CTRL", "CR{2}");
                    dict_setname_reg_to_reg.Add("ADC+FLAGS", "STAT{2}");
                    dict_setname_reg_to_reg.Add("USART1+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("USART2+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("USART1+IIR", "IIR{2}");
                    dict_setname_reg_to_reg.Add("USART2+IIR", "IIR{2}");
                    dict_setname_reg_to_reg.Add("USART1+LCR", "LCR{2}");
                    dict_setname_reg_to_reg.Add("USART2+LCR", "LCR{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                }
                #endregion
                #region LPC11AXX
                else if (subfamily.Name == "LPC11AXX")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //ADC
                            "SEL",
                            //SysTick
                            "STCTRL",
                            "STRELOAD",
                            "STCURR",
                            "STCALIB",
                            //WWDT
                            "WDTC",
                        });

                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_0_" + i.ToString(), "B" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_1_" + i.ToString(), "B" + (i + 32).ToString());
                    for (int i = 0; i <= 31;i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_0_" + i.ToString(), "W"+i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_1_" + i.ToString(), "W" + (i+32).ToString());
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "ADR");
                    for (int i = 0; i <= 3;i++ )
                        dict_set_reg_to_reg.Add("LPC_I2C_T+MASK_" + i, "MASK");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_0", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_1", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_2", "I");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_3", "I");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_4", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_5", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_6", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_7", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_8", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_9", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_10", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_11", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_12", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_13", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_14", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_15", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_16", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_17", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_18", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_19", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_20", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_21", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_22", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_23", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_24", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_25", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_26", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_27", "A");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_28", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_29", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_30", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_31", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_0", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_1", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_2", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_3", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_4", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_5", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_6", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_7", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_8", "D");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_9", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_10", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_11", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_12", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_13", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_14", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_15", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_16", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_17", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_18", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_19", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_20", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_21", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_22", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_23", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_24", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_25", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_26", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_27", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_28", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_29", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_30", "D");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO1_31", "D");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+BODCTRL", "BODR");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");

                    dict_set_to_set.Add("WWDT", "WD");

                    dict_setname_reg_to_reg.Add("ACMP+CTRL", "CTL");
                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TCR", "TCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TCR", "TCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_2", "CR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_2", "CR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_3", "CR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_3", "CR3{2}");
                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{2}");
                }
                #endregion
                #region LPC11CXX
                else if (subfamily.Name == "LPC11CXX")
                {
                    nonexisting_regs.AddRange(new string[] { 
                            "IOCON_PIO2_6",
                            "IOCON_PIO2_0",
                            "IOCON_RESET_PIO0_0",
                            "IOCON_PIO0_1",
                            "IOCON_PIO1_8",
                            "IOCON_PIO0_2",
                            "IOCON_PIO2_7",
                            "IOCON_PIO2_8",
                            "IOCON_PIO2_1",
                            "IOCON_PIO0_3",
                            "IOCON_PIO0_4",
                            "IOCON_PIO0_5",
                            "IOCON_PIO1_9",
                            "IOCON_PIO3_4",
                            "IOCON_PIO2_4",
                            "IOCON_PIO2_5",
                            "IOCON_PIO3_5",
                            "IOCON_PIO0_6",
                            "IOCON_PIO0_7",
                            "IOCON_PIO2_9",
                            "IOCON_PIO2_10",
                            "IOCON_PIO2_2",
                            "IOCON_PIO0_8",
                            "IOCON_PIO0_9",
                            "IOCON_SWCLK_PIO0_10",
                            "IOCON_PIO1_10",
                            "IOCON_PIO2_11",
                            "IOCON_R_PIO0_11",
                            "IOCON_R_PIO1_0",
                            "IOCON_R_PIO1_1",
                            "IOCON_R_PIO1_2",
                            "IOCON_PIO3_0",
                            "IOCON_PIO3_1",
                            "IOCON_PIO2_3",
                            "IOCON_SWDIO_PIO1_3",
                            "IOCON_PIO1_4",
                            "IOCON_PIO1_11",
                            "IOCON_PIO3_2",
                            "IOCON_PIO1_5",
                            "IOCON_PIO1_6",
                            "IOCON_PIO1_7",
                            "IOCON_PIO3_3",
                            "IOCON_SCK_LOC",
                            "IOCON_DSR_LOC",
                            "IOCON_DCD_LOC",
                            "IOCON_RI_LOC",
                            "IOCON_SCK0_LOC",
                            "IOCON_SSEL1_LOC",
                            "IOCON_CT16B0_CAP0_LOC",
                            "IOCON_SCK1_LOC",
                            "IOCON_MISO1_LOC",
                            "IOCON_MOSI1_LOC",
                            "IOCON_CT32B0_CAP0_LOC",
                            "IOCON_RXD_LOC",
                            "CANCNTL",
                            "CANSTAT",
                            "CANBT",
                            "CANINT",
                            "CANTEST",
                            "CANBRPE",
                            "CANIF1_CMDREQ",
                            "CANIF2_CMDREQ",
                            "CANIF1_CMDMSK_W",
                            "CANIF2_CMDMSK_W",
                            "CANIF1_CMDMSK_R",
                            "CANIF2_CMDMSK_R",
                            "CANIF1_MSK1",
                            "CANIF2_MASK1",
                            "CANIF1_MSK2",
                            "CANIF2_MASK2",
                            "CANIF1_ARB1",
                            "CANIF2_ARB1",
                            "CANIF1_ARB2",
                            "CANIF2_ARB2",
                            "CANIF1_MCTRL",
                            "CANIF2_MCTRL",
                            "CANIF1_DA1",
                            "CANIF2_DA1",
                            "CANIF1_DA2",
                            "CANIF2_DA2",
                            "CANIF1_DB1",
                            "CANIF2_DB1",
                            "CANIF1_DB2",
                            "CANIF2_DB2",
                            "CANTXREQ1",
                            "CANTXREQ2",
                            "CANND1",
                            "CANND2",
                            "CANIR1",
                            "CANIR2",
                            "CANMSGV1",
                            "CANMSGV2",
                            "CANCLKDIV",
                            "CANEC",
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB",
                            //PMU
                            "GPREG4",
                            //WDT
                            "WDWARNINT",
                            "WDWINDOW",
                            "WDTC{2}",
                            "WDMOD{2}",
                            //1100XL boards not handled!
                            "TMR16B0IR{2}",
                            "TMR16B1IR{2}",
                            "TMR16B0CCR{2}",
                            "TMR16B1CCR{2}",
                            "TMR16B0CTCR{2}",
                            "TMR16B1CTCR{2}",
                            "TMR32B0IR{2}",
                            "TMR32B1IR{2}",
                            "TMR32B0CCR{2}",
                            "TMR32B1CCR{2}",
                            "TMR32B0CTCR{2}",
                            "TMR32B1TCR{3}"
                        });

                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL", "CLKOUTCLKSEL");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTDIV", "CLKOUTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_FMC_T+FLASHTIM", "FLASHCFG");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "ADR");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "U0TER");

                    dict_set_to_set.Add("TIMER16_0", "TMR16B0");
                    dict_set_to_set.Add("TIMER16_1", "TMR16B1");
                    dict_set_to_set.Add("TIMER32_0", "TMR32B0");
                    dict_set_to_set.Add("TIMER32_1", "TMR32B1");
                    dict_set_to_set.Add("GPIO_PORT0", "GPIO0");
                    dict_set_to_set.Add("GPIO_PORT1", "GPIO1");
                    dict_set_to_set.Add("GPIO_PORT3", "GPIO3");
                    dict_set_to_set.Add("ADC", "AD0");
                    dict_set_to_set.Add("USART", "U0");
                    dict_set_to_set.Add("WWDT", "WD");
                    dict_set_to_set.Add("I2C", "I2C0");

                    dict_set_to_set.Add("SSP0", "SSP0");

                    dict_setname_reg_to_reg.Add("TIMER32_1+CTCR", "TMR32B1TCR{2}");
                }
                #endregion
                #region LPC11EXX
                else if (subfamily.Name == "LPC11EXX")
                {
                    nonexisting_regs.AddRange(new string[] {
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB",
                            //PMU
                            "GPREG4",
                        });

                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_0_" + i.ToString(), "B" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_1_" + i.ToString(), "B" + (i + 32).ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_0_" + i.ToString(), "W" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_1_" + i.ToString(), "W" + (i + 32).ToString());
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "ADR");
                    for (int i = 0; i <= 3; i++)
                        dict_set_reg_to_reg.Add("LPC_I2C_T+MASK_" + i, "MASK");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_0", "RESET_PIO0_0");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_10", "SWCLK_PIO0_10");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_11", "TDI_PIO0_11");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_12", "TMS_PIO0_12");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_13", "TDO_PIO0_13");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_14", "TRST_PIO0_14");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_15", "SWDIO_PIO0_15");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_FMC_T+FLASHTIM", "FLASHCFG");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");

                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                }
                #endregion
                #region LPC11UXX
                else if (subfamily.Name == "LPC11UXX")
                {
                    nonexisting_regs.AddRange(new string[] {
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB",
                            //PMU
                            "GPREG4",
                        });

                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_0_" + i.ToString(), "B" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_1_" + i.ToString(), "B" + (i + 32).ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_0_" + i.ToString(), "W" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_1_" + i.ToString(), "W" + (i + 32).ToString());
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "ADR");
                    for (int i = 0; i <= 3; i++)
                        dict_set_reg_to_reg.Add("LPC_I2C_T+MASK_" + i, "MASK");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_0", "RESET_PIO0_0");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_10", "SWCLK_PIO0_10");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_11", "TDI_PIO0_11");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_12", "TMS_PIO0_12");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_13", "TDO_PIO0_13");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_14", "TRST_PIO0_14");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_15", "SWDIO_PIO0_15");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_FMC_T+FLASHTIM", "FLASHCFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");

                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("ADC+INTEN", "INTEN{2}");
                }
                #endregion
                #region LPC11XXLV
                else if (subfamily.Name == "LPC11XXLV")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //IOCON
                            "RESET_PIO0_0",
                            "SWCLK_PIO0_10",
                            "PIO2_0",
                            "PIO0_1",
                            "PIO1_8",
                            "PIO0_2",
                            "PIO2_1",
                            "PIO0_3",
                            "PIO0_4",
                            "PIO0_5",
                            "PIO1_9",
                            "PIO3_4",
                            "PIO3_5",
                            "PIO0_6",
                            "PIO0_7",
                            "PIO0_8",
                            "PIO0_9",
                            "PIO1_10",
                            "R_PIO0_11",
                            "R_PIO1_0",
                            "R_PIO1_1",
                            "R_PIO1_2",
                            "SWDIO_PIO1_3",
                            "PIO1_4",
                            "PIO1_11",
                            "PIO1_5",
                            "PIO1_6",
                            "PIO1_7",
                            "SCK_LOC",
                            "RXD_LOC",
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB",
                        });
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL", "CLKOUTCLKSEL");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTDIV", "CLKOUTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_FMC_T+FLASHTIM", "FLASHCFG");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");

                    list_setname_reg_indexed.Add("LPC_GPIO_T+DATA");

                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("SSP0+RIS", "RIS{2}");
                    dict_setname_reg_to_reg.Add("SSP0+MIS", "MIS{2}");
                    dict_setname_reg_to_reg.Add("SSP1+RIS", "RIS{2}");
                    dict_setname_reg_to_reg.Add("SSP1+MIS", "MIS{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                }
                #endregion
                #endregion
                #region LPC12XX
                else if (subfamily.Name == "LPC12XX")
                {
                    nonexisting_regs.AddRange(new string[] {
                        //ADC
                        "TRM",
                        //FMC
                        "FLASHCFG",
                        //IOCON
                        "IOCON",
                        "IOCON{2}",
                        //SysTick
                        "SYST_CSR",
                        "SYST_RVR",
                        "SYST_CVR",
                        "SYST_CALIB"
                    });

                    dict_set_to_set.Add("DMA", "DMA_");

                    dict_set_reg_to_reg.Add("LPC_DMA_TypeDef+ALT_CTRL_BASE_PTR", "ATL_CTRL_BASE_PTR");
                    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+RESET_P0_13", "RESET_PIO0_13");
                    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO0_30", "R_PIO0_30");
                    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO0_31", "R_PIO0_31");
                    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO1_0", "R_PIO1_0");
                    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO1_1", "R_PIO1_1");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO0_10", "IOCON{2}");
                    //dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO0_11", "IOCON{2}");
                    //for (int i = 0; i <= 29; i++)
                    //{
                    //    if((i != 10) && (i!= 11))
                    //        dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO0_" + i, "IOCON");
                    //}
                    //for (int i = 2; i <= 6; i++)
                    //    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO1_" + i, "IOCON");
                    //for (int i = 0; i <= 15; i++)
                    //    dict_set_reg_to_reg.Add("LPC_IOCON_TypeDef+PIO2_" + i, "IOCON");
                    for (int i = 0; i <= 6; i++)
                        dict_set_reg_to_reg.Add("LPC_SYSCON_TypeDef+FILTERCLKCFG" + i, "IOCONFIGCLKDIV" + i);                                                          
                    dict_set_reg_to_reg.Add("LPC_UART0_TypeDef+ADRMATCH", "RS485ADRMATCH");                                                                              
                    dict_set_reg_to_reg.Add("LPC_WWDT_TypeDef+WDCLKSEL", "CLKSEL");

                    dict_setname_reg_to_reg.Add("RTC+IMSC", "ICSC");

                    dict_setname_reg_to_reg.Add("SSP+RIS", "RIS{2}");
                    dict_setname_reg_to_reg.Add("SSP+MIS", "MIS{2}");
                    dict_setname_reg_to_reg.Add("SSP+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("CT16B0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("CT16B1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("CT16B0+CR0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("CT16B1+CR0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("CT16B0+CR1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("CT16B1+CR1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("CT32B1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("CT32B0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("CT32B1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("CT32B0+CR_2", "CR2{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+CR_2", "CR2{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+CR_3", "CR3{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+CR_3", "CR3{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("CT32B0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("CT32B1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("RTC+DR", "DR{2}");
                    dict_setname_reg_to_reg.Add("RTC+RIS", "RIS{3}");
                    dict_setname_reg_to_reg.Add("RTC+MIS", "MIS{3}");
                    dict_setname_reg_to_reg.Add("RTC+ICR", "ICR{3}");
                    dict_setname_reg_to_reg.Add("ADC+CR", "CR{2}");
                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                    dict_setname_reg_to_reg.Add("UART1+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART1+IIR", "IIR{2}");
                    dict_setname_reg_to_reg.Add("UART1+LCR", "LCR{2}");
                }
                #endregion
                #region LPC13XX
                #region LPC1343
                else if(subfamily.Name == "LPC1343")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //IOCON
                            "IOCON_PIO2_6",
                            "IOCON_PIO2_0",
                            "IOCON_nRESET_PIO0_0",
                            "IOCON_PIO0_1",
                            "IOCON_PIO1_8",
                            "IOCON_PIO0_2",
                            "IOCON_PIO2_7",
                            "IOCON_PIO2_8",
                            "IOCON_PIO2_1",
                            "IOCON_PIO0_3",
                            "IOCON_PIO0_4",
                            "IOCON_PIO0_5",
                            "IOCON_PIO1_9",
                            "IOCON_PIO3_4",
                            "IOCON_PIO2_4",
                            "IOCON_PIO2_5",
                            "IOCON_PIO3_5",
                            "IOCON_PIO0_6",
                            "IOCON_PIO0_7",
                            "IOCON_PIO2_9",
                            "IOCON_PIO2_10",
                            "IOCON_PIO2_2",
                            "IOCON_PIO0_8",
                            "IOCON_PIO0_9",
                            "IOCON_SWCLK_PIO0_10",
                            "IOCON_PIO1_10",
                            "IOCON_PIO2_11",
                            "IOCON_R_PIO0_11",
                            "IOCON_R_PIO1_0",
                            "IOCON_R_PIO1_1",
                            "IOCON_R_PIO1_2",
                            "IOCON_PIO3_0",
                            "IOCON_PIO3_1",
                            "IOCON_PIO2_3",
                            "IOCON_SWDIO_PIO1_3",
                            "IOCON_PIO1_4",
                            "IOCON_PIO1_11",
                            "IOCON_PIO3_2",
                            "IOCON_PIO1_5",
                            "IOCON_PIO1_6",
                            "IOCON_PIO1_7",
                            "IOCON_PIO3_3",
                            "IOCON_SCK0_LOC",
                            "IOCON_DSR_LOC",
                            "IOCON_DCD_LOC",
                            "IOCON_RI_LOC",
                            //NVIC
                            "ISER0",
                            "ISER1",
                            "ICER0",
                            "ICER1",
                            "ISPR0",
                            "ISPR1",
                            "ICPR0",
                            "ICPR1",
                            "IABR0",
                            "IABR1",
                            "IPR0",
                            "IPR1",
                            "IPR2",
                            "IPR3",
                            "IPR4",
                            "IPR5",
                            "IPR6",
                            "IPR7",
                            "IPR8",
                            "IPR9",
                            "IPR10",
                            "IPR11",
                            "IPR12",
                            "IPR13",
                            "IPR14",
                            "STIR",
                            //PMU
                            "GPREG4",
                            //SysTick
                            "CTRL",
                            "LOAD",
                            "VAL",
                            "CALIB",
                            //Timers
                            "TMR16B0PWMC",
                            "TMR16B1PWMC",
                            "TMR32B0PWMC",
                            "TMR32B1PWMC",
                            //USB, according to headers not supported
                            "USBDevIntSt",
                            "USBDevIntEn",
                            "USBDevIntClr",
                            "USBDevIntSet",
                            "USBCmdCode",
                            "USBCmdData",
                            "USBRxData",
                            "USBTxData",
                            "USBRxPLen",
                            "USBTxPLen",
                            "USBCtrl",
                            "USBDevFIQSel",
                            //WDT
                            "WDWARNINT",
                            "WDWINDOW",
                            "WDTC{2}",
                            "WDMOD{2}"
                        });

                    dict_set_to_set.Add("I2C", "I2C0");
                    dict_set_to_set.Add("TIMER16_0", "TMR16B0");
                    dict_set_to_set.Add("TIMER16_1", "TMR16B1");
                    dict_set_to_set.Add("TIMER32_0", "TMR32B0");
                    dict_set_to_set.Add("TIMER32_1", "TMR32B1");
                    dict_set_to_set.Add("GPIO_PORT0", "GPIO0");
                    dict_set_to_set.Add("GPIO_PORT1", "GPIO1");
                    dict_set_to_set.Add("GPIO_PORT2", "GPIO2");
                    dict_set_to_set.Add("GPIO_PORT3", "GPIO3");
                    dict_set_to_set.Add("ADC", "AD0");
                    dict_set_to_set.Add("USART", "U0");
                    dict_set_to_set.Add("WWDT", "WD");

                    dict_set_to_set.Add("SSP0", "SSP0");

                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "I2C0ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "I2C0ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "I2C0ADR");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL", "CLKOUTCLKSEL");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+NMISRC", "INTNMI");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+SYSRSTSTAT", "SYSRESSTAT");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_0_STARTAPR", "STARTAPRP0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_0_STARTER", "STARTERP0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_0_STARTRSRCLR", "STARTRSRP0CLR");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_0_STARTSR", "STARTSRP0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_1_STARTAPR", "STARTAPRP1");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_1_STARTER", "STARTERP1");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_1_STARTRSRCLR", "STARTRSRP1CLR");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+STARTLOGIC_1_STARTSR", "STARTSRP1");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "U0TER");

                    dict_setname_reg_to_reg.Add("TIMER32_1+CTCR", "TMR32B1TCR{2}");
                }
                #endregion
                #region LPC1347
                else if (subfamily.Name == "LPC1347")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //PMU
                            "GPREG4",
                            //SysTick
                            "SYST_CSR",
                            "SYST_RVR",
                            "SYST_CVR",
                            "SYST_CALIB",
                            //TIMER
                            "PWMC",
                        });

                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_0_" + i.ToString(), "B" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_1_" + i.ToString(), "B" + (i + 32).ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_0_" + i.ToString(), "W" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_1_" + i.ToString(), "W" + (i + 32).ToString());
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "ADR");
                    for (int i = 0; i <= 3; i++)
                        dict_set_reg_to_reg.Add("LPC_I2C_T+MASK_" + i, "MASK");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_0", "RESET_PIO0_0");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_11", "TDI_PIO0_11");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_12", "TMS_PIO0_12");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_13", "TDO_PIO0_13");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_14", "TRST_PIO0_14");
                    dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO0_15", "SWDIO_PIO0_15");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USARTCLKDIV", "UARTCLKDIV");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID", "DEVICE_ID");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");

                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+IR", "IR{2}");
                    dict_setname_reg_to_reg.Add("TIMER16_1+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PR", "PR{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_0", "MR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_1", "MR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_2", "MR2{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+MR_3", "MR3{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_0", "CR0{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+CR_1", "CR1{3}");
                    dict_setname_reg_to_reg.Add("RITIMER+MASK", "MASK{2}");
                    dict_setname_reg_to_reg.Add("RITIMER+CTRL", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                }
                #endregion
                #endregion
                #region LPC15XX
                else if (subfamily.Name == "LPC15XX")
                {
                    nonexisting_regs.AddRange(new string[] {
                        //CCAN
                        "EC",
                        "CNTL",
                        "BT",
                        "INT",
                        "TEST",
                        "BRPE",
                        "IF1_CMDREQ",
                        "IF2_CMDREQ",
                        "IF1_CMDMSK_W",
                        "IF2_CMDMSK_W",
                        "IF1_CMDMSK_R",
                        "IF2_CMDMSK_R",
                        "IF1_MSK1",
                        "IF2_MASK1",
                        "IF1_MSK2",
                        "IF2_MASK2",
                        "IF1_ARB1",
                        "IF2_ARB1",
                        "IF1_ARB2",
                        "IF2_ARB2",
                        "IF1_MCTRL",
                        "IF2_MCTRL",
                        "IF1_DA1",
                        "IF2_DA1",
                        "IF1_DA2",
                        "IF2_DA2",
                        "IF1_DB1",
                        "IF2_DB1",
                        "IF1_DB2",
                        "IF2_DB2",
                        "TXREQ1",
                        "TXREQ2",
                        "ND1",
                        "ND2",
                        "IR1",
                        "IR2",
                        "MSGV1",
                        "MSGV2",
                        "STAT",
                        "CLKDIV{2}",
                        //NVIC
                        "ISER0",
                        "ISER1",
                        "ICER0",
                        "ICER1",
                        "ISPR0",
                        "ISPR1",
                        "ICPR0",
                        "ICPR1",
                        "IABR0",
                        "IABR1",
                        "STIR",
                        "IPR0",
                        "IPR1",
                        "IPR2",
                        "IPR3",
                        "IPR4",
                        "IPR5",
                        "IPR6",
                        "IPR7",
                        "IPR8",
                        "IPR9",
                        "IPR10",
                        "IPR11",
                        //PININT
                        "PMCTRL",
                        "PMSRC",
                        "PMCFG",
                        //QEI
                        "CON",
                        "CONF",
                        "POS",
                        "MAXPOS",
                        "CMPOS0",
                        "CMPOS1",
                        "CMPOS2",
                        "INXCNT",
                        "INXCMP0",
                        "LOAD",
                        "TIME",
                        "VEL",
                        "CAP",
                        "VELCOMP",
                        "FILTERPHA",
                        "FILTERPHB",
                        "FILTERINX",
                        "INXCMP1",
                        "INXCMP2",
                        "IEC",
                        "IES",
                        "IE",
                        "CLR",
                        "SET",
                        "INTSTAT{2}",
                        "STAT{5}",
                        //SCT, useless H and L subregisters and nonexisting registers
                        "FRACMAT0",
                        "FRACMAT1",
                        "FRACMAT2",
                        "FRACMAT3",
                        "FRACMAT4",
                        "FRACMAT5",
                        "MATCH15",
                        "CAPCTRL15",
                        "MATCHREL15",
                        "CAP15",
                        //SysTick
                        "SYST_CSR",
                        "SYST_RVR",
                        "SYST_CVR",
                        "SYST_CALIB",
                        //USART
                        "OSR",
                        "ADDR"
                    });

                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_0_" + i.ToString(), "B" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_1_" + i.ToString(), "B" + (i + 32).ToString());
                    for (int i = 0; i <= 11; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+B_2_" + i.ToString(), "B" + (i + 64).ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_0_" + i.ToString(), "W" + i.ToString());
                    for (int i = 0; i <= 31; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_1_" + i.ToString(), "W" + (i + 32).ToString());
                    for (int i = 0; i <= 11; i++)
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+W_2_" + i.ToString(), "W" + (i + 64).ToString());
                    dict_set_reg_to_reg.Add("LPC_ADC_T+SEQ_CTRL_0", "SEQA_CTRL");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+SEQ_CTRL_1", "SEQB_CTRL");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+SEQ_GDAT_0", "SEQA_GDAT");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+SEQ_GDAT_1", "SEQB_GDAT");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+THR_LOW_0", "THR0_LOW");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+THR_LOW_1", "THR1_LOW");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+THR_HIGH_0", "THR0_HIGH");
                    dict_set_reg_to_reg.Add("LPC_ADC_T+THR_HIGH_1", "THR1_HIGH");
                    for (int i = 0; i <= 11; i++)
                        dict_set_reg_to_reg.Add("LPC_ADC_T+DR_" + i.ToString(), "DAT" + i.ToString());
                    for (int i = 0; i <= 3; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_CMP_T+ACMP_" + i.ToString() + "_CMP", "CMP" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_CMP_T+ACMP_" + i.ToString() + "_CMPFILTR", "CMPFILTR" + i.ToString());
                    }
                    dict_set_reg_to_reg.Add("LPC_CRC_T+WRDATA32", "WR_DATA");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_ENABLESET", "ENABLESET0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_ENABLECLR", "ENABLECLR0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_ABORT", "ABORT0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_ACTIVE", "ACTIVE0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_BUSY", "BUSY0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_ERRINT", "ERRINT0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_INTENSET", "INTENSET0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_INTENCLR", "INTENCLR0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_INTA", "INTA0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_INTB", "INTB0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_SETVALID", "SETVALID0");
                    dict_set_reg_to_reg.Add("LPC_DMA_T+DMACOMMON_SETTRIG", "SETTRIG0");
                    for (int i = 0; i <= 17; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_DMA_T+DMACH_" + i.ToString() + "_CFG", "CFG" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_DMA_T+DMACH_" + i.ToString() + "_CTLSTAT", "CTLSTAT" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_DMA_T+DMACH_" + i.ToString() + "_XFERCFG", "XFERCFG" + i.ToString());
                    }
                    dict_set_reg_to_reg.Add("LPC_INMUX_T+DMA_INMUX_0", "DMA_INMUX_INMUX0");
                    dict_set_reg_to_reg.Add("LPC_INMUX_T+DMA_INMUX_1", "DMA_INMUX_INMUX1");
                    dict_set_reg_to_reg.Add("LPC_INMUX_T+DMA_INMUX_2", "DMA_INMUX_INMUX2");
                    dict_set_reg_to_reg.Add("LPC_INMUX_T+DMA_INMUX_3", "DMA_INMUX_INMUX3");
                    for (int j = 0; j <= 2; j++)
                        for (int i = 0; i <= 31; i++)
                            dict_set_reg_to_reg.Add("LPC_IOCON_T+PIO_" + j.ToString() + "_" + i.ToString(), "PIO" + j.ToString() + "_" + i.ToString());
                    for (int i = 0; i <= 3; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_INTVAL", "INTVAL" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_TIMER", "TIMER" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_CTRL", "CTRL" + i.ToString());
                        dict_set_reg_to_reg.Add("LPC_MRT_T+CHANNEL_" + i.ToString() + "_STAT", "STAT" + i.ToString());
                    }
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_0_ABORT_ENABLE", "ABORT_ENABLE0");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_1_ABORT_ENABLE", "ABORT_ENABLE1");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_2_ABORT_ENABLE", "ABORT_ENABLE2");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_3_ABORT_ENABLE", "ABORT_ENABLE3");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_0_ABORT_SOURCE", "ABORT_SOURCE0");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_1_ABORT_SOURCE", "ABORT_SOURCE1");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_2_ABORT_SOURCE", "ABORT_SOURCE2");
                    dict_set_reg_to_reg.Add("LPC_SCTIPU_T+ABORT_3_ABORT_SOURCE", "ABORT_SOURCE3");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL_0", "CLKOUTSELA");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CLKOUTSEL_1", "CLKOUTSELB");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PDWAKECFG", "PDAWAKECFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+JTAG_IDCODE", "JTAGIDCODE");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID_0", "DEVICE_ID0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DEVICEID_1", "DEVICE_ID1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+COUNT_U", "COUNT");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+DMA0REQUEST", "DMAREQ0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+DMA1REQUEST", "DMAREQ1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_1", "MATCH0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_2", "MATCH1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_3", "MATCH2");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_4", "MATCH3");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_5", "MATCH4");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_6", "MATCH5");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_7", "MATCH6");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_8", "MATCH7");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_9", "MATCH8");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_10", "MATCH9");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_11", "MATCH10");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_12", "MATCH11");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_13", "MATCH12");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_14", "MATCH13");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_15", "MATCH14");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_1", "CAP0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_2", "CAP1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_3", "CAP2");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_4", "CAP3");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_5", "CAP4");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_6", "CAP5");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_7", "CAP6");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_8", "CAP7");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_9", "CAP8");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_10", "CAP9");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_11", "CAP10");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_12", "CAP11");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_13", "CAP12");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_14", "CAP13");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_15", "CAP14");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_16", "CAP15");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_1", "MATCHREL0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_2", "MATCHREL1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_3", "MATCHREL2");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_4", "MATCHREL3");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_5", "MATCHREL4");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_6", "MATCHREL5");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_7", "MATCHREL6");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_8", "MATCHREL7");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_9", "MATCHREL8");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_10", "MATCHREL9");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_11", "MATCHREL10");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_12", "MATCHREL11");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_13", "MATCHREL12");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_14", "MATCHREL13");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_15", "MATCHREL14");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_16", "MATCHREL15");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_1", "CAPCTRL0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_2", "CAPCTRL1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_3", "CAPCTRL2");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_4", "CAPCTRL3");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_5", "CAPCTRL4");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_6", "CAPCTRL5");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_7", "CAPCTRL6");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_8", "CAPCTRL7");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_9", "CAPCTRL8");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_10", "CAPCTRL9");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_11", "CAPCTRL10");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_12", "CAPCTRL11");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_13", "CAPCTRL12");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_14", "CAPCTRL13");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_15", "CAPCTRL14");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_16", "CAPCTRL15");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+FRACMATREL_U_1", "FRACMATREL0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+FRACMATREL_U_2", "FRACMATREL1");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+FRACMATREL_U_3", "FRACMATREL2");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+FRACMATREL_U_4", "FRACMATREL3");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+FRACMATREL_U_5", "FRACMATREL4");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+FRACMATREL_U_6", "FRACMATREL5");
                    for (int i = 0; i <= 15; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_SCT_T+EVENT_STATE_" + (i + 1).ToString(), "EV" + i.ToString() + "_STATE");
                        dict_set_reg_to_reg.Add("LPC_SCT_T+EVENT_CTRL_" + (i + 1).ToString(), "EV" + i.ToString() + "_CTRL");
                    }
                    for (int i = 0; i <= 9; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_SET_" + (i + 1).ToString(), "OUT" + i.ToString() + "_SET");
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_CLR_" + (i + 1).ToString(), "OUT" + i.ToString() + "_CLR");
                    }
                    dict_set_reg_to_reg.Add("LPC_SPI_T+TXCTRL", "TXCTL");
                    dict_set_reg_to_reg.Add("LPC_USART_T+RXDATA_STAT", "RXDATSTAT");
                    dict_set_reg_to_reg.Add("LPC_USART_T+RXDATA", "RXDAT");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TXDATA", "TXDAT");

                    dict_setname_reg_to_reg.Add("USART0+CTRL", "CTL");
                    dict_setname_reg_to_reg.Add("USART1+CTRL", "CTL");
                    dict_setname_reg_to_reg.Add("USART2+CTRL", "CTL");
                    dict_setname_reg_to_reg.Add("USART0+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("USART1+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("USART2+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("DMA+CTRL", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("SCTLARGE_0+CTRL_U", "CTRL{3}");
                    dict_setname_reg_to_reg.Add("SCTLARGE_1+CTRL_U", "CTRL{3}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+CTRL_U", "CTRL{4}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+CTRL_U", "CTRL{4}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+INPUT", "INPUT{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+INPUT", "INPUT{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+OUTPUT", "OUTPUT{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+OUTPUT", "OUTPUT{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+OUTPUTDIRCTRL", "OUTPUTDIRCTRL{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+OUTPUTDIRCTRL", "OUTPUTDIRCTRL{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+RES", "RES{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+RES", "RES{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+CONEN", "CONEN{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+CONEN", "CONEN{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_0+CONFLAG", "CONFLAG{2}");
                    dict_setname_reg_to_reg.Add("SCTSMALL_1+CONFLAG", "CONFLAG{2}");
                    for (int i = 0; i <= 9; i++)
                    {
                        if (i <= 5)
                        {
                            dict_setname_reg_to_reg.Add("SCTSMALL_0+OUT_SET_" + (i + 1), "OUT" + i + "_SET{2}");
                            dict_setname_reg_to_reg.Add("SCTSMALL_1+OUT_SET_" + (i + 1), "OUT" + i + "_SET{2}");
                        }
                        if (i <= 7)
                        {
                            dict_setname_reg_to_reg.Add("SCTSMALL_0+CAPCTRL_U_" + (i + 1), "CAPCTRL" + i + "{2}");
                            dict_setname_reg_to_reg.Add("SCTSMALL_1+CAPCTRL_U_" + (i + 1), "CAPCTRL" + i + "{2}");
                        }
                        dict_setname_reg_to_reg.Add("SCTSMALL_0+EVENT_STATE_" + (i + 1), "EV" + i + "_STATE{2}");
                        dict_setname_reg_to_reg.Add("SCTSMALL_1+EVENT_STATE_" + (i + 1), "EV" + i + "_STATE{2}");
                    }
                    dict_setname_reg_to_reg.Add("RTC+CTRL", "CTRL{5}");
                    dict_setname_reg_to_reg.Add("RITIMER+CTRL", "CTRL{6}");
                    dict_setname_reg_to_reg.Add("ADC0+CTRL", "CTRL{7}");
                    dict_setname_reg_to_reg.Add("ADC1+CTRL", "CTRL{7}");
                    dict_setname_reg_to_reg.Add("ADC0+INTEN", "INTEN{2}");
                    dict_setname_reg_to_reg.Add("ADC1+INTEN", "INTEN{2}");
                    dict_setname_reg_to_reg.Add("DAC+CTRL", "CTRL{8}");
                    dict_setname_reg_to_reg.Add("USB+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("USART0+INTSTAT", "INTSTAT{4}");
                    dict_setname_reg_to_reg.Add("USART1+INTSTAT", "INTSTAT{4}");
                    dict_setname_reg_to_reg.Add("SPI0+INTSTAT", "INTSTAT{5}");
                    dict_setname_reg_to_reg.Add("SPI1+INTSTAT", "INTSTAT{5}");
                    dict_setname_reg_to_reg.Add("SPI0+CFG", "CFG{2}");
                    dict_setname_reg_to_reg.Add("SPI1+CFG", "CFG{2}");
                    dict_setname_reg_to_reg.Add("SPI0+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("SPI1+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("SPI0+INTENSET", "INTENSET{2}");
                    dict_setname_reg_to_reg.Add("SPI1+INTENSET", "INTENSET{2}");
                    dict_setname_reg_to_reg.Add("SPI0+INTENCLR", "INTENCLR{2}");
                    dict_setname_reg_to_reg.Add("SPI1+INTENCLR", "INTENCLR{2}");
                    dict_setname_reg_to_reg.Add("SPI0+RXDAT", "RXDAT{2}");
                    dict_setname_reg_to_reg.Add("SPI1+RXDAT", "RXDAT{2}");
                    dict_setname_reg_to_reg.Add("SPI0+TXDAT", "TXDAT{2}");
                    dict_setname_reg_to_reg.Add("SPI1+TXDAT", "TXDAT{2}");
                    dict_setname_reg_to_reg.Add("I2C+INTSTAT", "INTSTAT{6}");
                    dict_setname_reg_to_reg.Add("I2C+STAT", "STAT{4}");
                    dict_setname_reg_to_reg.Add("I2C+CFG", "CFG{3}");
                    dict_setname_reg_to_reg.Add("I2C+INTENSET", "INTENSET{3}");
                    dict_setname_reg_to_reg.Add("I2C+INTENCLR", "INTENCLR{3}");
                    dict_setname_reg_to_reg.Add("RTC+COUNT", "COUNT{2}");
                }
                #endregion
                #region LPC17XX_40XX
                #region LPC175X_6X
                else if (subfamily.Name == "LPC175X_6X")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //IP_CAN_001_CR_T
                            "CANTxSR",
                            "CANRxSR",
                            "CANMSR",
                            //NVIC
                            "ISER0",
                            "ISER1",
                            "ICER0",
                            "ICER1",
                            "ISPR0",
                            "ISPR1",
                            "ISPR1{2}",
                            "ICPR0",
                            "IABR0",
                            "IABR1",
                            "IPR0",
                            "IPR1",
                            "IPR2",
                            "IPR3",
                            "IPR4",
                            "IPR5",
                            "IPR6",
                            "IPR7",
                            "IPR8",
                            //PWM
                            "PWM1IR",
                            "PWM1TCR",
                            "PWM1CTCR",
                            "PWM1MCR",
                            "PWM1CCR",
                            "PWM1PCR",
                            "PWM1LER",
                            //SysTick
                            "STIR",
                            "STCTRL",
                            "STRELOAD",
                            "STCURR",
                            "STCALIB",
                            //OTG
                            "OTGIntSt",
                            "OTGStCtrl",
                            "OTGTmr",
                            "OTG_clock_control",
                            "USBIntSt{2}",
                            // Duplicates, equivalent CAN1 subregs used instead
                            "CAN2TFI1",
                            "CAN2TFI2",
                            "CAN2TFI3",
                            "CAN2TID1",
                            "CAN2TID2",
                            "CAN2TID3",
                            "CAN2TDA1",
                            "CAN2TDA2",
                            "CAN2TDA3",
                            "CAN2TDB1",
                            "CAN2TDB2",
                            "CAN2TDB3",
                            //SPI
                            "SPTSR",
                            "SPTCR",
                            //Unknown
                            "DMAReqSel",
                        });

                    dict_set_to_set.Add("ADC", "AD0");
                    dict_set_to_set.Add("DAC", "DA");
                    dict_set_to_set.Add("GPDMA", "DMAC");
                    dict_set_to_set.Add("GPIO0", "FIO0");
                    dict_set_to_set.Add("GPIO1", "FIO1");
                    dict_set_to_set.Add("GPIO2", "FIO2");
                    dict_set_to_set.Add("GPIO3", "FIO3");
                    dict_set_to_set.Add("GPIO4", "FIO4");
                    dict_set_to_set.Add("I2C0", "I2");
                    dict_set_to_set.Add("I2C1", "I2");
                    dict_set_to_set.Add("I2C2", "I2");
                    dict_set_to_set.Add("MCPWM", "MC");
                    dict_set_to_set.Add("RITIMER", "RI");
                    dict_set_to_set.Add("SPI", "S0SP");
                    dict_set_to_set.Add("TIMER0", "T0");
                    dict_set_to_set.Add("TIMER1", "T1");
                    dict_set_to_set.Add("TIMER2", "T2");
                    dict_set_to_set.Add("TIMER3", "T3");
                    dict_set_to_set.Add("UART0", "U0");
                    dict_set_to_set.Add("UART1", "U1");
                    dict_set_to_set.Add("WWDT", "WD");

                    dict_set_to_set.Add("CAN1", "CAN1");
                    dict_set_to_set.Add("CAN2", "CAN2");
                    dict_set_to_set.Add("I2S", "I2S");
                    dict_set_to_set.Add("QEI", "QEI");
                    dict_set_to_set.Add("SSP0", "SSP0");
                    dict_set_to_set.Add("USB", "USB");

                    dict_set_reg_to_reg.Add("LPC_CAN_T+RX_RFS", "CAN1RFS");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+RX_RID", "CAN1RID");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+RX_RD_0", "CAN1RDA");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+RX_RD_1", "CAN1RDB");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_0_TFI", "CAN1TFI1");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_1_TFI", "CAN1TFI2");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_2_TFI", "CAN1TFI3");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_0_TID", "CAN1TID1");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_1_TID", "CAN1TID2");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_2_TID", "CAN1TID3");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_0_TD_0", "CAN1TDA1");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_1_TD_0", "CAN1TDA2");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_2_TD_0", "CAN1TDA3");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_0_TD_1", "CAN1TDB1");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_1_TD_1", "CAN1TDB2");
                    dict_set_reg_to_reg.Add("LPC_CAN_T+TX_2_TD_1", "CAN1TDB3");
                    dict_set_reg_to_reg.Add("LPC_CANAF_T+LUTERR", "LUTerr");
                    dict_set_reg_to_reg.Add("LPC_DAC_T+CNTVAL", "DACCNTVAL");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAC1", "MAC1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAC2", "MAC2");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_IPGT", "IPGT");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_IPGR", "IPGR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_CLRT", "CLRT");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAXF", "MAXF");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_TEST", "TEST");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MCFG", "MCFG");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MCMD", "MCMD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MADR", "MADR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MWTD", "MWTD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MRDD", "MRDD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MIND", "MIND");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_0", "SA0");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_1", "SA1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_2", "SA2");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTSTATUS", "IntStatus");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTENABLE", "intEnable");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTCLEAR", "IntClear");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_CONTROL", "RxFilterCtrl");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_WOLSTATUS", "RxFilterWoLStatus");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_WOLCLEAR", "RxFilterWoLClear");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_HashFilterL", "HashFilterL");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_HashFilterH", "HashFilterH");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_COMMAND", "Command");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_STATUS", "Status");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TSV0", "TSV0");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TSV1", "TSV1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RSV", "RSV");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_FLOWCONTROLCOUNTER", "FlowControlCounter");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_FLOWCONTROLSTATUS", "FlowControlStatus");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+INTSTAT", "IntStat");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+INTTCSTAT", "IntTCStat");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+INTTCCLEAR", "IntTCClear");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+INTERRSTAT", "IntErrStat");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+INTERRCLR", "IntErrClr");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+RAWINTTCSTAT", "RawIntTCStat");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+RAWINTERRSTAT", "RawIntErrStat");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+ENBLDCHNS", "DMACEnbldChns");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SOFTBREQ", "DMACSoftBReq");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SOFTSREQ", "DMACSoftSReq");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SOFTLBREQ", "DMACSoftLBReq");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SOFTLSREQ", "DMACSoftLSReq");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CONFIG", "Config");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SYNC", "Sync");
                    for (int i = 0; i <= 7;i++ )
                    {
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_SRCADDR", "CxSrcAddr");
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_DESTADDR", "CxDestAddr");
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_CONTROL", "CxControl");
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_CONFIG", "CxConfig");
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_LLI", "CxLLI");
                    }
                    dict_set_reg_to_reg.Add("LPC_GPIOINT_T+STATUS", "IOIntStatus");
                    dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO0_CLR", "IO0IntClr");
                    dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO2_CLR", "IO2IntClr");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+INTF_SET", "PWMINTF_SET");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+INTF_CLR", "PWMINTF_CLR");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+CNTCON_CLR", "MCCAPCON_CLR{2}");
                    dict_set_reg_to_reg.Add("LPC_SPI_T+TSR", "SPTSR");
                    dict_set_reg_to_reg.Add("LPC_SSP_T+MIS", "SSPnMIS");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLCON", "PLL0CON");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLCFG", "PLL0CFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLSTAT", "PLL0STAT");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLFEED", "PLL0FEED");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLCON", "PLL1CON");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLCFG", "PLL1CFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLSTAT", "PLL1STAT");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLFEED", "PLL1FEED");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+CCLKSEL", "CCLKCFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+USBCLKSEL", "USBCLKCFG");
                    dict_set_reg_to_reg.Add("LPC_USB_T+EpInd", "EpIn");
                    dict_set_reg_to_reg.Add("LPC_USB_T+RxPLen", "RxPlen");

                    dict_setname_reg_to_reg.Add("UART0+TER1", "U0TER");
                    dict_setname_reg_to_reg.Add("UART1+TER1", "U1TER");
                    dict_setname_reg_to_reg.Add("UART2+TER1", "U0TER");
                    dict_setname_reg_to_reg.Add("UART3+TER1", "U0TER");
                    dict_setname_reg_to_reg.Add("DAC+CTRL", "DACCTRL");
                    dict_setname_reg_to_reg.Add("I2S+RXFIFO", "I2RXFIFO");
                    dict_setname_reg_to_reg.Add("I2S+TXRATE", "I2TXRATE");
                    dict_setname_reg_to_reg.Add("I2S+TXBITRATE", "I2TXBITRATE");
                    dict_setname_reg_to_reg.Add("I2S+DMA_0", "I2SDMA1");
                    dict_setname_reg_to_reg.Add("I2S+DMA_1", "I2SDMA2");
                    dict_setname_reg_to_reg.Add("MCPWM+CCP", "MCCP");
                    dict_setname_reg_to_reg.Add("SPI+CCR", "S0SPCCR");
                    dict_setname_reg_to_reg.Add("TIMER0+CCR", "T0CCR");
                    dict_setname_reg_to_reg.Add("TIMER1+CCR", "T1CCR");
                    dict_setname_reg_to_reg.Add("TIMER2+CCR", "T2CCR");
                    dict_setname_reg_to_reg.Add("TIMER3+CCR", "T3CCR");
                }
                #endregion
                #region LPC177X_8X
                else if (subfamily.Name == "LPC177X_8X")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //Central CAN?
                            "TXSR",
                            "RXSR",
                            "MSR{2}",
                            //CANAF
                            "SFF_SA",
                            "SFF_GRP_SA",
                            "EFF_SA",
                            "EFF_GRP_SA",
                            "ENDOFTABLE",
                            //NVIC
                            "ISER0",
                            "ISER1",
                            "ICER0",
                            "ICER1",
                            "ISPR0",
                            "ISPR1",
                            "ICPR0",
                            "ICPR1",
                            "IABR0",
                            "IABR1",
                            "IPR0",
                            "IPR1",
                            "IPR2",
                            "IPR3",
                            "IPR4",
                            "IPR5",
                            "IPR6",
                            "IPR7",
                            "IPR8",
                            "IPR9",
                            "IPR10",
                            "STIR",
                            //PWM
                            "MR4",
                            "MR5",
                            "MR6",
                            "PCR",
                            "LER",
                            "MCR{3}",
                            "MCR{3}",
                            "MCR{3}",
                            "CTCR{3}",
                            "IR{2}",
                            "TCR{2}",
                            "TC{2}",
                            "PR{2}",
                            "PC{2}",
                            "MCR{3}",
                            "MR0{2}",
                            "MR1{2}",
                            "MR2{2}",
                            "MR3{2}",
                            "CCR{2}",
                            "CR0{3}",
                            "CR1{3}",
                            "CTCR{2}",
                            //RTC 
                            "SEC",
                            "MIN",
                            "HRS",
                            "DOM",
                            "DOW",
                            "DOY",
                            "MONTH",
                            "YEAR",
                            "ASEC",
                            "AMIN",
                            "AHRS",
                            "ADOM",
                            "ADOW",
                            "ADOY",
                            "AMON",
                            "AYRS",
                            //SysTick
                            "STRELOAD",
                            "STCURR",
                            "STCALIB",
                            "STCTRL{2}",
                            //OTG
                            "I2C_WO",
                            "INTSET{2}",
                            "INTCLR{2}",
                            "STCTRL",
                            "INTEN"
                    });

                    dict_set_to_set.Add("CAN1", "CAN1");
                    dict_set_to_set.Add("CAN2", "CAN2");
                    dict_set_to_set.Add("EEPROM", "EE");

                    dict_set_reg_to_reg.Add("LPC_ADC_T+ADTRM", "TRM");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG0", "STATICWAITPAGE0");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG1", "STATICWAITPAGE1");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG2", "STATICWAITPAGE2");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG3", "STATICWAITPAGE3");
                    dict_set_reg_to_reg.Add("LPC_EEPROM_T+INTENSET", "ENSET");
                    dict_set_reg_to_reg.Add("LPC_EEPROM_T+INTENCLR", "ENCLR");
                    dict_set_reg_to_reg.Add("LPC_EEPROM_T+INTSTATSET", "STATSET");
                    dict_set_reg_to_reg.Add("LPC_EEPROM_T+INTSTATCLR", "STATCLR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAC1", "MAC1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAC2", "MAC2");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_IPGT", "IPGT");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_IPGR", "IPGR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_CLRT", "CLRT");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAXF", "MAXF");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SUPP", "SUPP");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_TEST", "TEST");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MCFG", "MCFG");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MCMD", "MCMD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MADR", "MADR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MWTD", "MWTD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MRDD", "MRDD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MIND", "MIND");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_0", "SA0");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_1", "SA1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_2", "SA2");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_CONTROL", "RXFILTERCTRL");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_WOLSTATUS", "RXFILTERWOLSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_WOLCLEAR", "RxFilterWoLClear");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_HashFilterL", "HASHFILTERL");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_HashFilterH", "HASHFILTERH");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_COMMAND", "COMMAND");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_STATUS", "STATUS");                    
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_FLOWCONTROLCOUNTER", "FLOWCONTROLCOUNTER");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_FLOWCONTROLSTATUS", "FLOWCONTROLSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TSV0", "TSV0");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TSV1", "TSV1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RSV", "RSV");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_DESCRIPTOR", "RXDESCRIPTOR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_STATUS", "RXSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_DESCRIPTORNUMBER", "RXDESCRIPTORNUMBER");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_PRODUCEINDEX", "RXPRODUCEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_CONSUMEINDEX", "RXCONSUMEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_DESCRIPTOR", "TXDESCRIPTOR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_STATUS", "TXSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_DESCRIPTORNUMBER", "TXDESCRIPTORNUMBER");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_PRODUCEINDEX", "TXPRODUCEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_CONSUMEINDEX", "TXCONSUMEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTSTATUS", "INTSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTENABLE", "INTENABLE");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTCLEAR", "INTCLEAR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_POWERDOWN", "POWERDOWN");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SOFTSREQ", "DMACSoftSReq");
                    for (int i = 0; i <= 7; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_SRCADDR", "SRCADDR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_DESTADDR", "DESTADDR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_CONTROL", "CONTROL" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_CONFIG", "CONFIG" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_LLI", "LLI" + i);
                    }
                    for (int i = 0; i <= 2; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_STATR", "STATR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_STATF", "STATF" + i);
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_ENR", "ENR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_ENF", "ENF" + i);
                    }

                    string[] type_a = new string[] { "0_12","0_13","0_23","0_24","0_25","0_26","1_30","1_31" };
                    for (int i = 0; i < type_a.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_a[i], "A");

                    string[] type_i = new string[] { "0_27","0_28", "5_2","5_3"};
                    for (int i = 0; i < type_i.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_i[i], "I");

                    string[] type_u = new string[] { "0_29", "0_30", "0_31"};
                    for (int i = 0; i < type_u.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_u[i], "U");

                    string[] type_w = new string[] { "0_7","0_8","0_9" };
                    for (int i = 0; i < type_w.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_w[i], "W");

                    for (int i = 0; i <= 5; i++)
                    {
                        for (int j = 0; j <= 31; j++)
                        {
                            if ((i == 5) && (j == 5))
                                break;

                            string entry = "LPC_IOCON_T+p_" + i + "_" + j;
                            if (!dict_set_reg_to_reg.ContainsKey(entry))
                                dict_set_reg_to_reg.Add(entry, "D");
                        }
                    }

                    dict_set_reg_to_reg.Add("LPC_SDC_T+POWER", "PWR");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_0", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_1", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_2", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_3", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_4", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_5", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_6", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_7", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_8", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_9", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_10", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_11", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_12", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_13", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_14", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_15", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLCON", "PLL0CON");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLCFG", "PLL0CFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLSTAT", "PLL0STAT");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLFEED", "PLL0FEED");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLCON", "PLL1CON");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLCFG", "PLL1CFG");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLSTAT", "PLL1STAT");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLFEED", "PLL1FEED");
                    dict_set_reg_to_reg.Add("LPC_USB_T+IntSt", "INTSTATUS");
                    dict_set_reg_to_reg.Add("LPC_USB_T+IntEn", "INTENABLE");
                    dict_set_reg_to_reg.Add("LPC_USB_T+IntClr", "INTCLEAR");

                    dict_setname_reg_to_reg.Add("CAN1+MOD", "CAN1MOD");
                    dict_setname_reg_to_reg.Add("CAN1+ICR", "CAN1ICR");
                    dict_setname_reg_to_reg.Add("CAN1+IER", "CAN1IER");
                    dict_setname_reg_to_reg.Add("CAN1+SR", "CAN1SR");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RFS", "CAN1RFS");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RID", "CAN1RID");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RD_0", "CAN1RDA");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RD_1", "CAN1RDB");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TFI", "CAN1TFI1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TFI", "CAN1TFI2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TFI", "CAN1TFI3");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TID", "CAN1TID1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TID", "CAN1TID2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TID", "CAN1TID3");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TD_0", "CAN1TDA1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TD_0", "CAN1TDA2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TD_0", "CAN1TDA3");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TD_1", "CAN1TDB1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TD_1", "CAN1TDB2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TD_1", "CAN1TDB3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TFI", "CAN2TFI1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TFI", "CAN2TFI2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TFI", "CAN2TFI3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TID", "CAN2TID1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TID", "CAN2TID2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TID", "CAN2TID3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TD_0", "CAN2TDA1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TD_0", "CAN2TDA2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TD_0", "CAN2TDA3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TD_1", "CAN2TDB1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TD_1", "CAN2TDB2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TD_1", "CAN2TDB3");
                    dict_setname_reg_to_reg.Add("GPIO0+DIR", "DIR0");
                    dict_setname_reg_to_reg.Add("GPIO0+PIN", "PIN0");
                    dict_setname_reg_to_reg.Add("GPIO0+SET", "SET0");
                    dict_setname_reg_to_reg.Add("GPIO0+CLR", "CLR0");
                    dict_setname_reg_to_reg.Add("GPIO0+MASK", "MASK0");
                    dict_setname_reg_to_reg.Add("GPIO1+DIR", "DIR1");
                    dict_setname_reg_to_reg.Add("GPIO1+PIN", "PIN1");
                    dict_setname_reg_to_reg.Add("GPIO1+SET", "SET1");
                    dict_setname_reg_to_reg.Add("GPIO1+CLR", "CLR1");
                    dict_setname_reg_to_reg.Add("GPIO1+MASK", "MASK1");
                    dict_setname_reg_to_reg.Add("GPIO2+DIR", "DIR2");
                    dict_setname_reg_to_reg.Add("GPIO2+PIN", "PIN2");
                    dict_setname_reg_to_reg.Add("GPIO2+SET", "SET2");
                    dict_setname_reg_to_reg.Add("GPIO2+CLR", "CLR2");
                    dict_setname_reg_to_reg.Add("GPIO2+MASK", "MASK2");
                    dict_setname_reg_to_reg.Add("GPIO3+DIR", "DIR3");
                    dict_setname_reg_to_reg.Add("GPIO3+PIN", "PIN3");
                    dict_setname_reg_to_reg.Add("GPIO3+SET", "SET3");
                    dict_setname_reg_to_reg.Add("GPIO3+CLR", "CLR3");
                    dict_setname_reg_to_reg.Add("GPIO3+MASK", "MASK3");
                    dict_setname_reg_to_reg.Add("GPIO4+DIR", "DIR4");
                    dict_setname_reg_to_reg.Add("GPIO4+PIN", "PIN4");
                    dict_setname_reg_to_reg.Add("GPIO4+SET", "SET4");
                    dict_setname_reg_to_reg.Add("GPIO4+CLR", "CLR4");
                    dict_setname_reg_to_reg.Add("GPIO4+MASK", "MASK4");
                    dict_setname_reg_to_reg.Add("GPIO5+DIR", "DIR5");
                    dict_setname_reg_to_reg.Add("GPIO5+PIN", "PIN5");
                    dict_setname_reg_to_reg.Add("GPIO5+SET", "SET5");
                    dict_setname_reg_to_reg.Add("GPIO5+CLR", "CLR5");
                    dict_setname_reg_to_reg.Add("GPIO5+MASK", "MASK5");
                    dict_setname_reg_to_reg.Add("I2S+DMA_0", "DMA1");
                    dict_setname_reg_to_reg.Add("I2S+DMA_1", "DMA2");
                    dict_setname_reg_to_reg.Add("MCPWM+CCP", "CP");
                    dict_setname_reg_to_reg.Add("CRC+WRDATA32", "DATA");

                    dict_setname_reg_to_reg.Add("DAC+CTRL", "CTRL{3}");
                    dict_setname_reg_to_reg.Add("DAC+CR", "CR{2}");
                    dict_setname_reg_to_reg.Add("EMC+STATUS", "STATUS{2}");
                    dict_setname_reg_to_reg.Add("GPIOINT+IO0_CLR", "CLR0{2}");
                    dict_setname_reg_to_reg.Add("GPIOINT+IO2_CLR", "CLR2{2}");
                    dict_setname_reg_to_reg.Add("ETHERNET+CONTROL_STATUS", "STATUS{3}");
                    dict_setname_reg_to_reg.Add("SDC+STATUS", "STATUS{4}");
                    dict_setname_reg_to_reg.Add("SDC+COMMAND", "COMMAND{2}");
                    dict_setname_reg_to_reg.Add("USB+Ctrl", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("USB+MASK0", "MASK0{2}");
                    dict_setname_reg_to_reg.Add("UART0+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART1+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART2+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART3+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART0+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART2+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART3+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART0+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART2+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART3+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART4+IER", "IER");
                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP2+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER2+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER3+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER2+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER3+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER2+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER3+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("MCPWM+INTEN", "INTEN{2}");
                    dict_setname_reg_to_reg.Add("ADC+INTEN", "INTEN{3}");
                    dict_setname_reg_to_reg.Add("WWDT+WINDOW", "WINDOW{2}");
                    dict_setname_reg_to_reg.Add("GPDMA+CONFIG", "CONFIG{2}");
                    dict_setname_reg_to_reg.Add("GPDMA+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("QEI+INTSTAT", "INTSTAT{2}");
                    dict_setname_reg_to_reg.Add("QEI+CON", "CON{2}");
                    dict_setname_reg_to_reg.Add("QEI+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("RTC+CCR", "CCR{3}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("SDC+MASK0", "MASK0{2}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_0", "MASK0{3}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_1", "MASK1{2}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_2", "MASK2{2}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_3", "MASK3{2}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_4", "MASK4{2}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_5", "MASK5{2}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_0", "MASK0{4}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_1", "MASK1{3}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_2", "MASK2{3}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_3", "MASK3{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_0", "MASK0{4}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_1", "MASK1{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_2", "MASK2{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_3", "MASK3{3}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_0", "MASK0{4}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_1", "MASK1{3}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_2", "MASK2{3}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_3", "MASK3{3}");
                    dict_setname_reg_to_reg.Add("EEPROM+INTEN", "INTEN{4}");
                    dict_setname_reg_to_reg.Add("EEPROM+INTSTAT", "STAT{4}");                    
                }
                #endregion
                #region LPC40XX
                else if (subfamily.Name == "LPC40XX")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //Central CAN?
                            "TXSR",
                            "RXSR",
                            "MSR{2}",
                            //CANAF
                            "SFF_SA",
                            "SFF_GRP_SA",
                            "EFF_SA",
                            "EFF_GRP_SA",
                            "ENDOFTABLE",
                            //NVIC
                            "ISER0",
                            "ISER1",
                            "ICER0",
                            "ICER1",
                            "ISPR0",
                            "ISPR1",
                            "ICPR0",
                            "ICPR1",
                            "IABR0",
                            "IABR1",
                            "IPR0",
                            "IPR1",
                            "IPR1{2}",
                            "IPR2",
                            "IPR3",
                            "IPR4",
                            "IPR5",
                            "IPR6",
                            "IPR7",
                            "IPR8",
                            "IPR9",
                            "STIR",
                            //PWM
                            "MR4",
                            "MR5",
                            "MR6",
                            "PCR",
                            "LER",
                            //RTC 
                            "SEC",
                            "MIN",
                            "HRS",
                            "DOM",
                            "DOW",
                            "DOY",
                            "MONTH",
                            "YEAR",
                            "ASEC",
                            "AMIN",
                            "AHRS",
                            "ADOM",
                            "ADOW",
                            "ADOY",
                            "AMON",
                            "AYRS",
                            //SysTick
                            "STRELOAD",
                            "STCURR",
                            "STCALIB",
                            "STCTRL{2}",
                            //OTG
                            "PORTSEL",
                            "CLKCTRL",
                            "CLKST{2}",
                            "INTSET{2}",
                            "INTCLR{2}",
                            "CLKCTRL{2}",
                            //PWM
                            "MR4",
                            "MR5",
                            "MR6",
                            "PCR",
                            "LER",
                            "MCR{3}",
                            "MCR{3}",
                            "MCR{3}",
                            "CTCR{3}",
                            "IR{2}",
                            "TCR{2}",
                            "TC{2}",
                            "PR{2}",
                            "PC{2}",
                            "MCR{3}",
                            "MR0{2}",
                            "MR1{2}",
                            "MR2{2}",
                            "MR3{2}",
                            "CCR{2}",
                            "CR0{3}",
                            "CR1{3}",
                            "CTCR{2}",
                    });

                    dict_set_to_set.Add("CAN1", "CAN1");
                    dict_set_to_set.Add("CAN2", "CAN2");
                    dict_set_to_set.Add("USB", "USB");

                    dict_set_reg_to_reg.Add("LPC_ADC_T+ADTRM", "TRM");
                    dict_set_reg_to_reg.Add("LPC_CMP_T+CMP_CTRLx_0", "CMP_CTRL0");
                    dict_set_reg_to_reg.Add("LPC_CMP_T+CMP_CTRLx_1", "CMP_CTRL1");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG0", "STATICWAITPAGE0");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG1", "STATICWAITPAGE1");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG2", "STATICWAITPAGE2");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG3", "STATICWAITPAGE3");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAC1", "MAC1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAC2", "MAC2");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_IPGT", "IPGT");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_IPGR", "IPGR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_CLRT", "CLRT");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MAXF", "MAXF");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_TEST", "TEST");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MCFG", "MCFG");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MCMD", "MCMD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MADR", "MADR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MWTD", "MWTD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MRDD", "MRDD");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_MIND", "MIND");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_0", "SA0");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_1", "SA1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SA_2", "SA2");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MAC_SUPP", "SUPP");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_FLOWCONTROLCOUNTER", "FLOWCONTROLCOUNTER");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_FLOWCONTROLSTATUS", "FLOWCONTROLSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TSV0", "TSV0");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TSV1", "TSV1");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RSV", "RSV");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_DESCRIPTOR", "RXDESCRIPTOR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_STATUS", "RXSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_CONTROL", "RXFILTERCTRL");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_WOLSTATUS", "RXFILTERWOLSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_WOLCLEAR", "RxFilterWoLClear");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_HashFilterL", "HASHFILTERL");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+RXFILTER_HashFilterH", "HASHFILTERH");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTSTATUS", "INTSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTENABLE", "INTENABLE");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_INTCLEAR", "INTCLEAR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+MODULE_CONTROL_POWERDOWN", "POWERDOWN");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_DESCRIPTORNUMBER", "RXDESCRIPTORNUMBER");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_PRODUCEINDEX", "RXPRODUCEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_RX_CONSUMEINDEX", "RXCONSUMEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_DESCRIPTOR", "TXDESCRIPTOR");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_STATUS", "TXSTATUS");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_DESCRIPTORNUMBER", "TXDESCRIPTORNUMBER");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_PRODUCEINDEX", "TXPRODUCEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_TX_CONSUMEINDEX", "TXCONSUMEINDEX");
                    dict_set_reg_to_reg.Add("LPC_ENET_T+CONTROL_COMMAND", "COMMAND");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+SOFTSREQ", "DMACSoftSReq");
                    for (int i = 0; i <= 7; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_SRCADDR", "SRCADDR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_DESTADDR", "DESTADDR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_CONTROL", "CONTROL" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_CONFIG", "CONFIG" + i);
                        dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_" + i + "_LLI", "LLI" + i);
                    }

                    string[] type_a = new string[] { "0_12", "0_13", "0_23", "0_24", "0_25", "0_26", "1_30", "1_31" };
                    for (int i = 0; i < type_a.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_a[i], "A");

                    string[] type_i = new string[] { "0_27", "0_28", "5_2", "5_3" };
                    for (int i = 0; i < type_i.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_i[i], "I");

                    string[] type_u = new string[] { "0_29", "0_30", "0_31" };
                    for (int i = 0; i < type_u.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_u[i], "U");

                    string[] type_w = new string[] { "0_7", "0_8", "0_9", "1_5", "1_6", "1_7", "1_14", "1_16", "1_17" };
                    for (int i = 0; i < type_w.Length; i++)
                        dict_set_reg_to_reg.Add("LPC_IOCON_T+p_" + type_w[i], "W");

                    for (int i = 0; i <= 5; i++)
                    {
                        for (int j = 0; j <= 31; j++)
                        {
                            if ((i == 5) && (j == 5))
                                break;

                            string entry = "LPC_IOCON_T+p_" + i + "_" + j;
                            if (!dict_set_reg_to_reg.ContainsKey(entry))
                                dict_set_reg_to_reg.Add(entry, "D");
                        }
                    }


                    for (int i = 0; i <= 2; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_STATR", "STATR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_STATF", "STATF" + i);
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_ENR", "ENR" + i);
                        dict_set_reg_to_reg.Add("LPC_GPIOINT_T+IO" + i + "_ENF", "ENF" + i);
                    }
                    dict_set_reg_to_reg.Add("LPC_SDC_T+POWER", "PWR");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_0", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_1", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_2", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_3", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_4", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_5", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_6", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_7", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_8", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_9", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_10", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_11", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_12", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_13", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_14", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SDC_T+FIFO_15", "FIFO");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLCON", "PLLCON0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLCFG", "PLLCFG0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLSTAT", "PLLSTAT0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_0_PLLFEED", "PLLFEED0");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLCON", "PLLCON1");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLCFG", "PLLCFG1");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLSTAT", "PLLSTAT1");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+PLL_1_PLLFEED", "PLLFEED1");
                    dict_set_reg_to_reg.Add("LPC_SYSCTL_T+DMAREQSEL", "DMACReqSel");
                    dict_set_reg_to_reg.Add("LPC_USB_T+EpInd", "EPIN");
                    dict_set_reg_to_reg.Add("LPC_USB_T+USBClkSt", "CLKST");

                    dict_setname_reg_to_reg.Add("CAN1+MOD", "CAN1MOD");
                    dict_setname_reg_to_reg.Add("CAN1+ICR", "CAN1ICR");
                    dict_setname_reg_to_reg.Add("CAN1+IER", "CAN1IER");
                    dict_setname_reg_to_reg.Add("CAN1+SR", "CAN1SR");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RFS", "CAN1RFS");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RID", "CAN1RID");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RD_0", "CAN1RDA");
                    dict_setname_reg_to_reg.Add("CAN1+RX_RD_1", "CAN1RDB");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TFI", "CAN1TFI1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TFI", "CAN1TFI2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TFI", "CAN1TFI3");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TID", "CAN1TID1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TID", "CAN1TID2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TID", "CAN1TID3");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TD_0", "CAN1TDA1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TD_0", "CAN1TDA2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TD_0", "CAN1TDA3");
                    dict_setname_reg_to_reg.Add("CAN1+TX_0_TD_1", "CAN1TDB1");
                    dict_setname_reg_to_reg.Add("CAN1+TX_1_TD_1", "CAN1TDB2");
                    dict_setname_reg_to_reg.Add("CAN1+TX_2_TD_1", "CAN1TDB3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TFI", "CAN2TFI1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TFI", "CAN2TFI2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TFI", "CAN2TFI3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TID", "CAN2TID1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TID", "CAN2TID2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TID", "CAN2TID3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TD_0", "CAN2TDA1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TD_0", "CAN2TDA2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TD_0", "CAN2TDA3");
                    dict_setname_reg_to_reg.Add("CAN2+TX_0_TD_1", "CAN2TDB1");
                    dict_setname_reg_to_reg.Add("CAN2+TX_1_TD_1", "CAN2TDB2");
                    dict_setname_reg_to_reg.Add("CAN2+TX_2_TD_1", "CAN2TDB3");
                    dict_setname_reg_to_reg.Add("CRC+WRDATA32", "DATA");
                    dict_setname_reg_to_reg.Add("GPIO0+DIR", "DIR0");
                    dict_setname_reg_to_reg.Add("GPIO0+PIN", "PIN0");
                    dict_setname_reg_to_reg.Add("GPIO0+SET", "SET0");
                    dict_setname_reg_to_reg.Add("GPIO0+CLR", "CLR0");
                    dict_setname_reg_to_reg.Add("GPIO1+DIR", "DIR1");
                    dict_setname_reg_to_reg.Add("GPIO1+PIN", "PIN1");
                    dict_setname_reg_to_reg.Add("GPIO1+SET", "SET1");
                    dict_setname_reg_to_reg.Add("GPIO1+CLR", "CLR1");
                    dict_setname_reg_to_reg.Add("GPIO2+DIR", "DIR2");
                    dict_setname_reg_to_reg.Add("GPIO2+PIN", "PIN2");
                    dict_setname_reg_to_reg.Add("GPIO2+SET", "SET2");
                    dict_setname_reg_to_reg.Add("GPIO2+CLR", "CLR2");
                    dict_setname_reg_to_reg.Add("GPIO3+DIR", "DIR3");
                    dict_setname_reg_to_reg.Add("GPIO3+PIN", "PIN3");
                    dict_setname_reg_to_reg.Add("GPIO3+SET", "SET3");
                    dict_setname_reg_to_reg.Add("GPIO3+CLR", "CLR3");
                    dict_setname_reg_to_reg.Add("GPIO4+DIR", "DIR4");
                    dict_setname_reg_to_reg.Add("GPIO4+PIN", "PIN4");
                    dict_setname_reg_to_reg.Add("GPIO4+SET", "SET4");
                    dict_setname_reg_to_reg.Add("GPIO4+CLR", "CLR4");
                    dict_setname_reg_to_reg.Add("GPIO5+DIR", "DIR5");
                    dict_setname_reg_to_reg.Add("GPIO5+PIN", "PIN5");
                    dict_setname_reg_to_reg.Add("GPIO5+SET", "SET5");
                    dict_setname_reg_to_reg.Add("GPIO5+CLR", "CLR5");
                    dict_setname_reg_to_reg.Add("I2S+DMA_0", "DMA1");
                    dict_setname_reg_to_reg.Add("I2S+DMA_1", "DMA2");
                    dict_setname_reg_to_reg.Add("MCPWM+CCP", "CP");

                    dict_setname_reg_to_reg.Add("DAC+CTRL", "CTRL{3}");
                    dict_setname_reg_to_reg.Add("DAC+CR", "CR{2}");
                    dict_setname_reg_to_reg.Add("EMC+STATUS", "STATUS{2}");
                    dict_setname_reg_to_reg.Add("GPIOINT+IO0_CLR", "CLR0{2}");
                    dict_setname_reg_to_reg.Add("GPIOINT+IO2_CLR", "CLR2{2}");
                    dict_setname_reg_to_reg.Add("ETHERNET+CONTROL_STATUS", "STATUS{3}");
                    dict_setname_reg_to_reg.Add("SDC+STATUS", "STATUS{4}");
                    dict_setname_reg_to_reg.Add("SDC+COMMAND", "COMMAND{2}");
                    dict_setname_reg_to_reg.Add("USB+Ctrl", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("USB+MASK0", "MASK0{2}");
                    dict_setname_reg_to_reg.Add("UART0+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART1+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART2+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART3+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART0+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART2+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART3+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART0+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART2+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART3+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART4+IER", "IER");
                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP2+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("TIMER0+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER2+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER3+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("TIMER0+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER1+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER2+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER3+CR_0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("TIMER0+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER1+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER2+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER3+CR_1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+PC", "PC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_0+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("TIMER32_1+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("ADC+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("MCPWM+INTEN", "INTEN{2}");
                    dict_setname_reg_to_reg.Add("ADC+INTEN", "INTEN{3}");
                    dict_setname_reg_to_reg.Add("WWDT+WINDOW", "WINDOW{2}");
                    dict_setname_reg_to_reg.Add("GPDMA+CONFIG", "CONFIG{2}");
                    dict_setname_reg_to_reg.Add("GPDMA+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("QEI+INTSTAT", "INTSTAT{2}");
                    dict_setname_reg_to_reg.Add("QEI+CON", "CON{2}");
                    dict_setname_reg_to_reg.Add("QEI+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("RTC+CCR", "CCR{3}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{2}");
                    dict_setname_reg_to_reg.Add("CANAF_RAM+MASK_0", "MASK0{2}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_0", "MASK0{3}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_1", "MASK1{2}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_2", "MASK2{2}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_3", "MASK3{2}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_0", "MASK0{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_1", "MASK1{2}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_2", "MASK2{2}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_3", "MASK3{2}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_0", "MASK0{3}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_1", "MASK1{2}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_2", "MASK2{2}");
                    dict_setname_reg_to_reg.Add("I2C2+MASK_3", "MASK3{2}");
                    dict_setname_reg_to_reg.Add("EEPROM+INTEN", "INTEN{4}");
                    dict_setname_reg_to_reg.Add("EEPROM+INTSTAT", "INTSTAT{4}");
                }
                #endregion
                #endregion
                #region LPC18XX_43XX
                #region LPC18XX
                else if (subfamily.Name == "LPC18XX")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //CCAN, read direction subs, write dir ones used instead
                            "IF1_CMDMSK_R",
                            "IF2_CMDMSK_R",
                            //CCU
                            "CLK_EMCDIV_CFG",
                            //RTC 
                            "SEC",
                            "MIN",
                            "HRS",
                            "DOM",
                            "DOW",
                            "DOY",
                            "MONTH",
                            "YEAR",
                            "ASEC",
                            "AMIN",
                            "AHRS",
                            "ADOM",
                            "ADOW",
                            "ADOY",
                            "AMON",
                            "AYRS",
                            //SCT, useless H and L subregisters
                            "CAP",
                            "STATE",
                            "LIMIT",
                            "HALT",
                            "STOP",
                            "START",
                            "REGMODE",
                            "EVSTATEMSK",
                            "EVSTATEMSK0",
                            "EVSTATEMSK1",
                            "EVSTATEMSK2",
                            "EVSTATEMSK3",
                            "EVSTATEMSK4",
                            "EVSTATEMSK5",
                            "EVSTATEMSK6",
                            "EVSTATEMSK7",
                            "EVSTATEMSK8",
                            "EVSTATEMSK9",
                            "EVSTATEMSK10",
                            "EVSTATEMSK11",
                            "EVSTATEMSK12",
                            "EVSTATEMSK13",
                            "EVSTATEMSK14",
                            "EVSTATEMSK15",
                            "FRACMAT0",
                            "FRACMAT1",
                            "FRACMAT2",
                            "FRACMAT3",
                            "FRACMAT4",
                            "FRACMAT5",
                            "MATCH",
                            "FRACMATREL0",
                            "FRACMATREL1",
                            "FRACMATREL2",
                            "FRACMATREL3",
                            "FRACMATREL4",
                            "FRACMATREL5",
                            "DITHER",//does not exist
                            //SCT, ditherless subregisters, prefer dithered registers here as it is a superset
                            "CONFIG{3}",
                            "EVCTRL",
                            //SCU
                            "SDDELAY",
                            //SPIFI
                            "ADDR",
                            "IDATA",
                            "CLIMIT",
                            "DATA",
                            "MCMD",
                            "CTRL{3}",
                            "CMD{2}",
                            //CCU2
                            "CLK_XXX_CFG{2}",
                            "CLK_XXX_STAT{2}"
                    });

                    dict_set_to_set.Add("CCU1", "CCU1");
                    dict_set_to_set.Add("CCU2", "CCU2");

                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_CMDREQ", "IF1_CMDREQ");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_CMDREQ", "IF2_CMDREQ");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_DA1", "IF1_DA1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_DA2", "IF1_DA2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_DB1", "IF1_DB1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_DB2", "IF1_DB2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_DA1", "IF2_DA1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_DA2", "IF2_DA2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_DB1", "IF2_DB1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_DB2", "IF2_DB2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_MSK1", "IF1_MSK1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_MSK2", "IF1_MSK2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_MSK1", "IF2_MSK1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_MSK2", "IF2_MSK2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_ARB1", "IF1_ARB1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_ARB2", "IF1_ARB2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_ARB1", "IF2_ARB1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_ARB2", "IF2_ARB2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_CMDMSK", "IF1_CMDMSK_W");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_CMDMSK", "IF2_CMDMSK_W");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_MCTRL", "IF1_MCTRL");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_MCTRL", "IF2_MCTRL");
                    for (int i = 0; i < 322; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_CCU1_T+CLKCCU_" + i.ToString() + "_CFG", "CLK_XXX_CFG");
                        dict_set_reg_to_reg.Add("LPC_CCU1_T+CLKCCU_" + i.ToString() + "_STAT", "CLK_XXX_STAT");
                        dict_set_reg_to_reg.Add("LPC_CCU2_T+CLKCCU_" + i.ToString() + "_CFG", "CLK_XXX_CFG");
                        dict_set_reg_to_reg.Add("LPC_CCU2_T+CLKCCU_" + i.ToString() + "_STAT", "CLK_XXX_STAT");
                    }
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_STAT", "PLL0USB_STAT");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_CTRL", "PLL0USB_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_MDIV", "PLL0USB_MDIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_NP_DIV", "PLL0USB_NP_DIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_STAT", "PLL0AUDIO_STAT");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_CTRL", "PLL0AUDIO_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_MDIV", "PLL0AUDIO_MDIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_NP_DIV", "PLL0AUDIO_NP_DIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_0", "IDIVA_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_1", "IDIVB_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_2", "IDIVC_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_3", "IDIVD_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_4", "IDIVE_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_0", "BASE_SAFE_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_1", "BASE_USB0_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_3", "BASE_USB1_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_4", "BASE_M3_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_20", "BASE_OUT_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_25", "BASE_AUDIO_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_26", "BASE_CGU_OUT0_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_27", "BASE_CGU_OUT1_CLK");
                    dict_set_reg_to_reg.Add("LPC_CREG_T+MXMEMMAP", "M3MEMMAP");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG0", "STATICWAITPAGE0");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG1", "STATICWAITPAGE1");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG2", "STATICWAITPAGE2");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG3", "STATICWAITPAGE3");
                    for (int i = 0; i <= 3; i++)
                        for (int j = 0; j <= 3; j++ )
                            dict_set_reg_to_reg.Add("LPC_GIMA_T+CAP0_IN_" + i + "_" + j, "CAP" + i + "_" + j + "_IN");
                    for (int i = 0; i <= 7;i++ )
                        dict_set_reg_to_reg.Add("LPC_GIMA_T+CTIN_IN_" + i, "CTIN_" + i + "_IN");                        
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_SRCADDR", "SRCADDR0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_SRCADDR", "SRCADDR1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_SRCADDR", "SRCADDR2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_SRCADDR", "SRCADDR3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_SRCADDR", "SRCADDR4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_SRCADDR", "SRCADDR5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_SRCADDR", "SRCADDR6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_SRCADDR", "SRCADDR7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_DESTADDR", "DESTADDR0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_DESTADDR", "DESTADDR1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_DESTADDR", "DESTADDR2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_DESTADDR", "DESTADDR3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_DESTADDR", "DESTADDR4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_DESTADDR", "DESTADDR5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_DESTADDR", "DESTADDR6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_DESTADDR", "DESTADDR7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_CONTROL", "CONTROL0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_CONTROL", "CONTROL1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_CONTROL", "CONTROL2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_CONTROL", "CONTROL3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_CONTROL", "CONTROL4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_CONTROL", "CONTROL5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_CONTROL", "CONTROL6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_CONTROL", "CONTROL7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_CONFIG", "CONFIG0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_CONFIG", "CONFIG1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_CONFIG", "CONFIG2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_CONFIG", "CONFIG3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_CONFIG", "CONFIG4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_CONFIG", "CONFIG5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_CONFIG", "CONFIG6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_CONFIG", "CONFIG7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_LLI", "LLI0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_LLI", "LLI1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_LLI", "LLI2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_LLI", "LLI3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_LLI", "LLI4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_LLI", "LLI5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_LLI", "LLI6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_LLI", "LLI7");
                    for (int i = 0; i <= 7; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+MASK_" + i, "MASK");
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+SET_" + i, "SET");
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+CLR_" + i, "CLR");
                    }
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR0", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR1", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR2", "ADR");
                    dict_set_reg_to_reg.Add("LPC_I2C_T+ADR3", "ADR");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+CCP", "CP");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+LIM_0", "LIM");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+LIM_1", "LIM");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+LIM_2", "LIM");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+MAT_0", "MAT");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+MAT_1", "MAT");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+MAT_2", "MAT");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+COUNT_U", "COUNT");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+DMA0REQUEST", "DMAREQ0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+DMA1REQUEST", "DMAREQ1");
                    for (int i = 0; i <= 15; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_" + (i + 1), "MATCH" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_" + (i + 1), "CAP" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_" + (i + 1), "MATCHREL" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_" + (i + 1), "CAPCTRL" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+EVENT_CTRL_" + (i + 1), "EVCTRL{2}");
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_SET_" + (i + 1), "OUTPUTSET" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_CLR_" + (i + 1), "OUTPUTCL" + i);
                    }
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSP_3_3", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_0", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_1", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_2", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_3", "SFS{3}");
                    for (int i = 1; i <= 10; i++)
                    {
                        for (int j = 0; j <= 31; j++)
                        {
                            if ((i == 1) && (j < 17))
                                continue;
                            if ((i == 10) && (j > 3))
                                break;
                            if (!dict_set_reg_to_reg.ContainsKey("LPC_SCU_T+SFSP_" + i + "_" + j))
                                dict_set_reg_to_reg.Add("LPC_SCU_T+SFSP_" + i + "_" + j, "SFS{2}");
                        }
                    }
                    for (int i = 0; i <= 15; i++)
                    {
                        for (int j = 0; j <= 31; j++)
                        {
                            if (!dict_set_reg_to_reg.ContainsKey("LPC_SCU_T+SFSP_" + i + "_" + j))
                                dict_set_reg_to_reg.Add("LPC_SCU_T+SFSP_" + i + "_" + j, "SFS");
                        }
                    }
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER2", "TER");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_0", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_1", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_2", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_3", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_4", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_5", "ENDPTCTRL");

                    dict_setname_reg_to_reg.Add("I2S0+DMA_0", "DMA1");
                    dict_setname_reg_to_reg.Add("I2S0+DMA_1", "DMA2");
                    dict_setname_reg_to_reg.Add("I2S1+DMA_0", "DMA1");
                    dict_setname_reg_to_reg.Add("I2S1+DMA_1", "DMA2");

                    list_setname_reg_indexed.Add("LPC_LCD_T+PAL");
                    list_setname_reg_indexed.Add("LPC_LCD_T+CRSR_IMG");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+B");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+W");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+DIR");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+MPIN");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+PIN");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+NOT");
                    list_setname_reg_indexed.Add("LPC_GPIOGROUPINT_T+PORT_POL");
                    list_setname_reg_indexed.Add("LPC_GPIOGROUPINT_T+PORT_ENA");

                    dict_setname_reg_to_reg.Add("SDMMC+CTRL", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("SDMMC+STATUS", "STATUS{2}");
                    dict_setname_reg_to_reg.Add("EMC+STATUS", "STATUS{3}");
                    dict_setname_reg_to_reg.Add("EMC+CONFIG", "CONFIG{2}");
                    dict_setname_reg_to_reg.Add("LCD+CTRL", "CTRL{4}");
                    dict_setname_reg_to_reg.Add("LCD+INTSTAT", "INTSTAT{2}");
                    dict_setname_reg_to_reg.Add("SCT+CONFIG", "CONFIG{4}");
                    dict_setname_reg_to_reg.Add("SCT+CTRL_U", "CTRL{5}");
                    dict_setname_reg_to_reg.Add("MCPWM+TC_0", "TC{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+TC_1", "TC{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+TC_2", "TC{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+CAP_0", "CAP{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+CAP_1", "CAP{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+CAP_2", "CAP{2}");
                    dict_setname_reg_to_reg.Add("QEI+CON", "CON{2}");
                    dict_setname_reg_to_reg.Add("QEI+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("QEI+CAP", "CAP{3}");
                    dict_setname_reg_to_reg.Add("QEI+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("QEI+CLR", "CLR{2}");
                    dict_setname_reg_to_reg.Add("QEI+SET", "SET{2}");
                    dict_setname_reg_to_reg.Add("RITIMER+MASK", "MASK{2}");
                    dict_setname_reg_to_reg.Add("RITIMER+CTRL", "CTRL{6}");
                    dict_setname_reg_to_reg.Add("ATIMER+CLR_EN", "CLR_EN{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+SET_EN", "SET_EN{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+STATUS", "STATUS{4}");
                    dict_setname_reg_to_reg.Add("ATIMER+ENABLE", "ENABLE{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+CLR_STAT", "CLR_STAT{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+SET_STAT", "SET_STAT{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                    dict_setname_reg_to_reg.Add("WWDT+WINDOW", "WINDOW{2}");
                    dict_setname_reg_to_reg.Add("RTC+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("UART1+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART1+IIR", "IIR{2}");
                    dict_setname_reg_to_reg.Add("UART1+FCR", "FCR{2}");
                    dict_setname_reg_to_reg.Add("UART1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("UART1+ACR", "ACR{2}");
                    dict_setname_reg_to_reg.Add("UART1+LSR", "LSR{2}");
                    dict_setname_reg_to_reg.Add("UART1+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART1+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART1+TER2", "TER");
                    dict_setname_reg_to_reg.Add("SSP0+CR0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("SSP1+CR0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("SSP0+CR1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("SSP1+CR1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("I2S0+STATE", "STATE{2}");
                    dict_setname_reg_to_reg.Add("I2S1+STATE", "STATE{2}");
                    dict_setname_reg_to_reg.Add("C_CAN0+STAT", "STAT{4}");
                    dict_setname_reg_to_reg.Add("C_CAN1+STAT", "STAT{4}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_3", "ENDPTCTRL{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_1", "ENDPTCTRL{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_2", "ENDPTCTRL{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBCMD_D", "USBCMD_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBSTS_D", "USBSTS_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBSTS_H", "USBSTS_H{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBINTR_D", "USBINTR_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTNAK", "ENDPTNAK{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTNAKEN", "ENDPTNAKEN{2}");
                    dict_setname_reg_to_reg.Add("USB1+PORTSC1_D", "PORTSC1_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+PORTSC1_H", "PORTSC1_H{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBMODE_H", "USBMODE_H{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTSETUPSTAT", "ENDPTSETUPSTAT{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTPRIME", "ENDPTPRIME{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTFLUSH", "ENDPTFLUSH{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTSTAT", "ENDPTSTAT{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCOMPLETE", "ENDPTCOMPLETE{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_0", "ENDPTCTRL0{2}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_0", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_0", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_1", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_1", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_2", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_2", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C0+MASK_3", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C1+MASK_3", "MASK{3}");
                    dict_setname_reg_to_reg.Add("I2C0+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("I2C1+STAT", "STAT{3}");
                }
                #endregion
                #region LPC43XX
                else if (subfamily.Name == "LPC43XX")
                {
                    nonexisting_regs.AddRange(new string[] {
                            //CCAN, read direction subs, write dir ones used instead
                            "IF1_CMDMSK_R",
                            "IF2_CMDMSK_R",
                            //CCU
                            "CLK_EMCDIV_CFG",
                            "CLK_XXX_CFG{2}",
                            "CLK_XXX_STAT{2}",
                            //RTC 
                            "SEC",
                            "MIN",
                            "HRS",
                            "DOM",
                            "DOW",
                            "DOY",
                            "MONTH",
                            "YEAR",
                            "ASEC",
                            "AMIN",
                            "AHRS",
                            "ADOM",
                            "ADOW",
                            "ADOY",
                            "AMON",
                            "AYRS",
                            //SCT, useless H and L subregisters
                            "CAP",
                            "STATE",
                            "LIMIT",
                            "HALT",
                            "STOP",
                            "START",
                            "REGMODE",
                            "EVSTATEMSK",
                            "EVSTATEMSK0",
                            "EVSTATEMSK1",
                            "EVSTATEMSK2",
                            "EVSTATEMSK3",
                            "EVSTATEMSK4",
                            "EVSTATEMSK5",
                            "EVSTATEMSK6",
                            "EVSTATEMSK7",
                            "EVSTATEMSK8",
                            "EVSTATEMSK9",
                            "EVSTATEMSK10",
                            "EVSTATEMSK11",
                            "EVSTATEMSK12",
                            "EVSTATEMSK13",
                            "EVSTATEMSK14",
                            "EVSTATEMSK15",
                            "FRACMAT0",
                            "FRACMAT1",
                            "FRACMAT2",
                            "FRACMAT3",
                            "FRACMAT4",
                            "FRACMAT5",
                            "MATCH",
                            "FRACMATREL0",
                            "FRACMATREL1",
                            "FRACMATREL2",
                            "FRACMATREL3",
                            "FRACMATREL4",
                            "FRACMATREL5",
                            "DITHER",//does not exist
                            //SCT, non-dithered registers
                            "EVCTRL",
                            "CONFIG{3}",
                            //SPI
                            "TSR",
                            "TCR{2}",
                            //SPIFI
                            "ADDR",
                            "IDATA",
                            "CLIMIT",
                            "DATA",
                            "MCMD",
                            "CTRL{3}",
                            "CMD{2}"
                    });

                    dict_set_to_set.Add("CCU1", "CCU1");
                    dict_set_to_set.Add("CCU2", "CCU2");

                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_CMDREQ", "IF1_CMDREQ");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_CMDREQ", "IF2_CMDREQ");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_MSK1", "IF1_MSK1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_MSK2", "IF1_MSK2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_MSK1", "IF2_MSK1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_MSK2", "IF2_MSK2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_ARB1", "IF1_ARB1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_ARB2", "IF1_ARB2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_ARB1", "IF2_ARB1");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_ARB2", "IF2_ARB2");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_CMDMSK", "IF1_CMDMSK_W");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_CMDMSK", "IF2_CMDMSK_W");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_0_MCTRL", "IF1_MCTRL");
                    dict_set_reg_to_reg.Add("LPC_CCAN_T+IF_1_MCTRL", "IF2_MCTRL");
                    for (int i = 0; i < 322; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_CCU1_T+CLKCCU_" + i.ToString() + "_CFG", "CLK_XXX_CFG");
                        dict_set_reg_to_reg.Add("LPC_CCU1_T+CLKCCU_" + i.ToString() + "_STAT", "CLK_XXX_STAT");
                        dict_set_reg_to_reg.Add("LPC_CCU2_T+CLKCCU_" + i.ToString() + "_CFG", "CLK_XXX_CFG{2}");
                        dict_set_reg_to_reg.Add("LPC_CCU2_T+CLKCCU_" + i.ToString() + "_STAT", "CLK_XXX_STAT{2}");
                    }
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_STAT", "PLL0USB_STAT");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_CTRL", "PLL0USB_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_MDIV", "PLL0USB_MDIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_0_PLL_NP_DIV", "PLL0USB_NP_DIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_STAT", "PLL0AUDIO_STAT");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_CTRL", "PLL0AUDIO_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_MDIV", "PLL0AUDIO_MDIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+PLL_1_PLL_NP_DIV", "PLL0AUDIO_NP_DIV");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_0", "IDIVA_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_1", "IDIVB_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_2", "IDIVC_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_3", "IDIVD_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+IDIV_CTRL_4", "IDIVE_CTRL");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_0", "BASE_SAFE_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_1", "BASE_USB0_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_2", "BASE_PERIPH_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_3", "BASE_USB1_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_4", "BASE_M4_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_20", "BASE_OUT_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_25", "BASE_APLL_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_26", "BASE_CGU_OUT0_CLK");
                    dict_set_reg_to_reg.Add("LPC_CGU_T+BASE_CLK_27", "BASE_CGU_OUT1_CLK");
                    dict_set_reg_to_reg.Add("LPC_CREG_T+MXMEMMAP", "M4MEMMAP");
                    dict_set_reg_to_reg.Add("LPC_CREG_T+M0SUBTXEVENT", "M0TXEVENT");
                    for (int i = 0; i <= 3; i++)
                        for (int j = 0; j <= 3; j++)
                            dict_set_reg_to_reg.Add("LPC_GIMA_T+CAP0_IN_" + i + "_" + j, "CAP" + i + "_" + j + "_IN");
                    for (int i = 0; i <= 7; i++)
                        dict_set_reg_to_reg.Add("LPC_GIMA_T+CTIN_IN_" + i, "CTIN_" + i + "_IN");
                    dict_set_reg_to_reg.Add("LPC_GIMA_T+ADCHS_TRIGGER_IN", "VADC_TRIGGER_IN");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_SRCADDR", "SRCADDR0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_SRCADDR", "SRCADDR1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_SRCADDR", "SRCADDR2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_SRCADDR", "SRCADDR3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_SRCADDR", "SRCADDR4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_SRCADDR", "SRCADDR5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_SRCADDR", "SRCADDR6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_SRCADDR", "SRCADDR7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_DESTADDR", "DESTADDR0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_DESTADDR", "DESTADDR1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_DESTADDR", "DESTADDR2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_DESTADDR", "DESTADDR3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_DESTADDR", "DESTADDR4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_DESTADDR", "DESTADDR5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_DESTADDR", "DESTADDR6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_DESTADDR", "DESTADDR7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_CONTROL", "CONTROL0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_CONTROL", "CONTROL1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_CONTROL", "CONTROL2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_CONTROL", "CONTROL3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_CONTROL", "CONTROL4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_CONTROL", "CONTROL5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_CONTROL", "CONTROL6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_CONTROL", "CONTROL7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_CONFIG", "CONFIG0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_CONFIG", "CONFIG1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_CONFIG", "CONFIG2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_CONFIG", "CONFIG3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_CONFIG", "CONFIG4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_CONFIG", "CONFIG5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_CONFIG", "CONFIG6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_CONFIG", "CONFIG7");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_0_LLI", "LLI0");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_1_LLI", "LLI1");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_2_LLI", "LLI2");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_3_LLI", "LLI3");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_4_LLI", "LLI4");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_5_LLI", "LLI5");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_6_LLI", "LLI6");
                    dict_set_reg_to_reg.Add("LPC_GPDMA_T+CH_7_LLI", "LLI7");
                    for (int i = 0; i <= 7; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+MASK_" + i, "MASK");
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+SET_" + i, "SET");
                        dict_set_reg_to_reg.Add("LPC_GPIO_T+CLR_" + i, "CLR");
                    }
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG0", "STATICWAITPAGE0");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG1", "STATICWAITPAGE1");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG2", "STATICWAITPAGE2");
                    dict_set_reg_to_reg.Add("LPC_EMC_T+STATICWAITPAG3", "STATICWAITPAGE3");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+CCP", "CP");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+LIM_0", "LIM");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+LIM_1", "LIM");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+LIM_2", "LIM");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+MAT_0", "MAT");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+MAT_1", "MAT");
                    dict_set_reg_to_reg.Add("LPC_MCPWM_T+MAT_2", "MAT");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+COUNT_U", "COUNT");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+DMA0REQUEST", "DMAREQ0");
                    dict_set_reg_to_reg.Add("LPC_SCT_T+DMA1REQUEST", "DMAREQ1");
                    for (int i = 0; i <= 15; i++)
                    {
                        dict_set_reg_to_reg.Add("LPC_SCT_T+MATCH_U_" + (i + 1), "MATCH" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+CAP_U_" + (i + 1), "CAP" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+MATCHREL_U_" + (i + 1), "MATCHREL" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+CAPCTRL_U_" + (i + 1), "CAPCTRL" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+EVENT_CTRL_" + (i + 1), "EVCTRL{2}");
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_SET_" + (i + 1), "OUTPUTSET" + i);
                        dict_set_reg_to_reg.Add("LPC_SCT_T+OUT_CLR_" + (i + 1), "OUTPUTCL" + i);
                    }
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSP_3_3", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_0", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_1", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_2", "SFS{3}");
                    dict_set_reg_to_reg.Add("LPC_SCU_T+SFSCLK_3", "SFS{3}");
                    for (int i = 1; i <= 10; i++)
                    {
                        for (int j = 0; j <= 31; j++)
                        {
                            if ((i == 1) && (j < 17))
                                continue;
                            if ((i == 10) && (j > 3))
                                break;
                            if (!dict_set_reg_to_reg.ContainsKey("LPC_SCU_T+SFSP_" + i + "_" + j))
                                dict_set_reg_to_reg.Add("LPC_SCU_T+SFSP_" + i + "_" + j, "SFS{2}");
                        }
                    }
                    for (int i = 0; i <= 15; i++)
                    {
                        for (int j = 0; j <= 31; j++)
                        {
                            if (!dict_set_reg_to_reg.ContainsKey("LPC_SCU_T+SFSP_" + i + "_" + j))
                                dict_set_reg_to_reg.Add("LPC_SCU_T+SFSP_" + i + "_" + j, "SFS");
                        }
                    }
                    dict_set_reg_to_reg.Add("LPC_SGPIO_T+CTR_STATUS_0", "CLR_STATUS_0");
                    dict_set_reg_to_reg.Add("LPC_SGPIO_T+CTR_STATUS_1", "CLR_STATUS_1");
                    dict_set_reg_to_reg.Add("LPC_SGPIO_T+CTR_STATUS_2", "CLR_STATUS_2");
                    dict_set_reg_to_reg.Add("LPC_SGPIO_T+CTR_STATUS_3", "CLR_STATUS_3");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER1", "TER");
                    dict_set_reg_to_reg.Add("LPC_USART_T+TER2", "TER");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_0", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_1", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_2", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_3", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_4", "ENDPTCTRL");
                    dict_set_reg_to_reg.Add("LPC_USBHS_T+ENDPTCTRL_5", "ENDPTCTRL");

                    dict_setname_reg_to_reg.Add("I2S0+DMA_0", "DMA1");
                    dict_setname_reg_to_reg.Add("I2S0+DMA_1", "DMA2");
                    dict_setname_reg_to_reg.Add("I2S1+DMA_0", "DMA1");
                    dict_setname_reg_to_reg.Add("I2S1+DMA_1", "DMA2");

                    list_setname_reg_indexed.Add("LPC_LCD_T+PAL");
                    list_setname_reg_indexed.Add("LPC_LCD_T+CRSR_IMG");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+B");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+W");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+DIR");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+MPIN");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+PIN");
                    list_setname_reg_indexed.Add("LPC_GPIO_T+NOT");
                    list_setname_reg_indexed.Add("LPC_GPIOGROUPINT_T+PORT_POL");
                    list_setname_reg_indexed.Add("LPC_GPIOGROUPINT_T+PORT_ENA");

                    dict_setname_reg_to_reg.Add("SDMMC+CTRL", "CTRL{2}");
                    dict_setname_reg_to_reg.Add("SDMMC+STATUS", "STATUS{2}");
                    dict_setname_reg_to_reg.Add("EMC+STATUS", "STATUS{3}");
                    dict_setname_reg_to_reg.Add("EMC+CONFIG", "CONFIG{2}");
                    dict_setname_reg_to_reg.Add("LCD+CTRL", "CTRL{4}");
                    dict_setname_reg_to_reg.Add("LCD+INTSTAT", "INTSTAT{2}");
                    dict_setname_reg_to_reg.Add("SCT+CONFIG", "CONFIG{4}");
                    dict_setname_reg_to_reg.Add("SCT+CTRL_U", "CTRL{5}");
                    dict_setname_reg_to_reg.Add("MCPWM+TC_0", "TC{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+TC_1", "TC{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+TC_2", "TC{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+CAP_0", "CAP{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+CAP_1", "CAP{2}");
                    dict_setname_reg_to_reg.Add("MCPWM+CAP_2", "CAP{2}");
                    dict_setname_reg_to_reg.Add("QEI+CON", "CON{2}");
                    dict_setname_reg_to_reg.Add("QEI+STAT", "STAT{2}");
                    dict_setname_reg_to_reg.Add("QEI+CAP", "CAP{3}");
                    dict_setname_reg_to_reg.Add("QEI+INTSTAT", "INTSTAT{3}");
                    dict_setname_reg_to_reg.Add("QEI+CLR", "CLR{2}");
                    dict_setname_reg_to_reg.Add("QEI+SET", "SET{2}");
                    dict_setname_reg_to_reg.Add("RITIMER+MASK", "MASK{2}");
                    dict_setname_reg_to_reg.Add("RITIMER+CTRL", "CTRL{6}");
                    dict_setname_reg_to_reg.Add("ATIMER+CLR_EN", "CLR_EN{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+SET_EN", "SET_EN{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+STATUS", "STATUS{4}");
                    dict_setname_reg_to_reg.Add("ATIMER+ENABLE", "ENABLE{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+CLR_STAT", "CLR_STAT{2}");
                    dict_setname_reg_to_reg.Add("ATIMER+SET_STAT", "SET_STAT{2}");
                    dict_setname_reg_to_reg.Add("WWDT+TC", "TC{3}");
                    dict_setname_reg_to_reg.Add("WWDT+WINDOW", "WINDOW{2}");
                    dict_setname_reg_to_reg.Add("RTC+CCR", "CCR{2}");
                    dict_setname_reg_to_reg.Add("UART1+IER", "IER{2}");
                    dict_setname_reg_to_reg.Add("UART1+IIR", "IIR{2}");
                    dict_setname_reg_to_reg.Add("UART1+FCR", "FCR{2}");
                    dict_setname_reg_to_reg.Add("UART1+MCR", "MCR{2}");
                    dict_setname_reg_to_reg.Add("UART1+LSR", "LSR{2}");
                    dict_setname_reg_to_reg.Add("UART1+RS485CTRL", "RS485CTRL{2}");
                    dict_setname_reg_to_reg.Add("UART1+TER1", "TER");
                    dict_setname_reg_to_reg.Add("UART1+TER2", "TER");
                    dict_setname_reg_to_reg.Add("SSP0+CR0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("SSP1+CR0", "CR0{2}");
                    dict_setname_reg_to_reg.Add("SSP0+CR1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("SSP1+CR1", "CR1{2}");
                    dict_setname_reg_to_reg.Add("SSP0+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SSP1+ICR", "ICR{2}");
                    dict_setname_reg_to_reg.Add("SPI+SR", "SR{2}");
                    dict_setname_reg_to_reg.Add("SPI+DR", "DR{2}");
                    dict_setname_reg_to_reg.Add("SPI+CCR", "CCR{3}");
                    dict_setname_reg_to_reg.Add("SPI+INT", "INT{2}");
                    dict_setname_reg_to_reg.Add("I2S0+STATE", "STATE{2}");
                    dict_setname_reg_to_reg.Add("I2S1+STATE", "STATE{2}");
                    dict_setname_reg_to_reg.Add("C_CAN0+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("C_CAN1+STAT", "STAT{3}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_3", "ENDPTCTRL{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_1", "ENDPTCTRL{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_2", "ENDPTCTRL{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBCMD_D", "USBCMD_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBSTS_D", "USBSTS_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBSTS_H", "USBSTS_H{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBINTR_D", "USBINTR_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTNAK", "ENDPTNAK{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTNAKEN", "ENDPTNAKEN{2}");
                    dict_setname_reg_to_reg.Add("USB1+PORTSC1_D", "PORTSC1_D{2}");
                    dict_setname_reg_to_reg.Add("USB1+PORTSC1_H", "PORTSC1_H{2}");
                    dict_setname_reg_to_reg.Add("USB1+USBMODE_H", "USBMODE_H{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTSETUPSTAT", "ENDPTSETUPSTAT{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTPRIME", "ENDPTPRIME{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTFLUSH", "ENDPTFLUSH{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTSTAT", "ENDPTSTAT{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCOMPLETE", "ENDPTCOMPLETE{2}");
                    dict_setname_reg_to_reg.Add("USB1+ENDPTCTRL_0", "ENDPTCTRL0{2}");
                }
                #endregion
                #endregion
                // Create a hardware register set for each base address with the correctly calculated addresses
                List<HardwareRegisterSet> sets = new List<HardwareRegisterSet>();
                foreach (var peripheral in subfamily.SetBaseAddresses)
                {
                    if(peripheral.Type == "LPC_USART_T" && !registerset_types.ContainsKey(peripheral.Type))
                          peripheral.Type = "LPC_USART0_T";

                    
                    if(!registerset_types.ContainsKey(peripheral.Type))
                    {
                        HardwareRegisterSet set1 = new HardwareRegisterSet();
                        continue;
                    }
                    

                    HardwareRegisterSet set = DeepCopy(registerset_types[peripheral.Type]);
                    set.UserFriendlyName = peripheral.Name;

                    if (!used_types.Contains(peripheral.Type))
                        used_types.Add(peripheral.Type);

                    foreach (var register in set.Registers)
                    {
                        // Fix the register addresses
                        register.Address = FormatToHex(ParseHex(register.Address) + peripheral.Address);

                        if(dict_setname_reg_to_reg.ContainsKey(set.UserFriendlyName + "+" + register.Name))
                        {
                            string key = set.UserFriendlyName + "+" + register.Name;
                            if (subregisters[dict_setname_reg_to_reg[key]] != null)
                                register.SubRegisters = subregisters[dict_setname_reg_to_reg[key]].ToArray();
                            used_subregisters.Add(dict_setname_reg_to_reg[key]);// Still subregisters found, just set to null
                        }
                        //Add the subregisters
                        else if (subregisters.ContainsKey(register.Name))
                        {
                            if (subregisters[register.Name] != null)
                                register.SubRegisters = subregisters[register.Name].ToArray();
                            used_subregisters.Add(register.Name);// Still subregisters found, just set to null
                        }
                        else if (subregisters.ContainsKey(register.Name.ToUpper()))
                        {
                            if (subregisters[register.Name.ToUpper()] != null)
                                register.SubRegisters = subregisters[register.Name.ToUpper()].ToArray();
                            used_subregisters.Add(register.Name.ToUpper());// Still subregisters found, just set to null
                        }
                        else
                        {
                            // 1. Maybe the indexing is done differently
                            if(register.Name.Contains('_'))
                            {
                                Match indexed_name_m;
                                if((indexed_name_m = indexed_name.Match(register.Name)).Success)
                                {
                                    // only try to fix indexing with a sinle index
                                    if(indexed_name_m.Groups[3].ToString() == "")
                                    {
                                        string no_underscore_indexed_reg = indexed_name_m.Groups[1].ToString() + indexed_name_m.Groups[2].ToString();
                                        if (subregisters.ContainsKey(no_underscore_indexed_reg))
                                        {
                                            if (subregisters[no_underscore_indexed_reg] != null)
                                                register.SubRegisters = subregisters[no_underscore_indexed_reg].ToArray();
                                            used_subregisters.Add(no_underscore_indexed_reg);// Still subregisters found, just set to null
                                            continue;// Subregisters found
                                        }
                                    }

                                    // Reuse subregisters from a non-indexed register
                                    string non_indexed_reg = indexed_name_m.Groups[1].ToString();
                                    if (list_setname_reg_indexed.Contains(peripheral.Type + "+" + non_indexed_reg))
                                    {
                                        if (subregisters[non_indexed_reg] != null)
                                            register.SubRegisters = subregisters[non_indexed_reg].ToArray();
                                        used_subregisters.Add(non_indexed_reg);// Still subregisters found, just set to null
                                        continue;// Subregisters found
                                    }
                                }
                            }


                            // 2. Try matching from the known different register names dictionary
                            string diff_reg_key = peripheral.Type + "+" + register.Name;
                            if (dict_set_reg_to_reg.ContainsKey(diff_reg_key) && subregisters.ContainsKey(dict_set_reg_to_reg[diff_reg_key]))
                            {
                                if (subregisters[dict_set_reg_to_reg[diff_reg_key]] != null)
                                    register.SubRegisters = subregisters[dict_set_reg_to_reg[diff_reg_key]].ToArray();
                                used_subregisters.Add(dict_set_reg_to_reg[diff_reg_key]);// Still subregisters found, just set to null
                                continue;
                            }

                            // 3. Try different namings from the set names dictionary
                            if (dict_set_to_set.ContainsKey(set.UserFriendlyName))
                            {
                                //Let's try to locate the subregisters further by trying different name patterns
                                List<string> fuzzy_reg_names = new List<string>() { dict_set_to_set[set.UserFriendlyName] + register.Name, dict_set_to_set[set.UserFriendlyName] + "_" + register.Name }; // Starts with corrected set name

                                if (dict_set_reg_to_reg.ContainsKey(diff_reg_key))
                                    fuzzy_reg_names.Add(dict_set_to_set[set.UserFriendlyName] + dict_set_reg_to_reg[diff_reg_key]);

                                Match indexed_name_m;
                                if ((indexed_name_m = indexed_name.Match(register.Name)).Success)
                                {
                                    // Only try to fix registers with a single index
                                    if (indexed_name_m.Groups[3].ToString() == "")
                                    {
                                        string no_underscore_indexed_reg = indexed_name_m.Groups[1].ToString() + indexed_name_m.Groups[2].ToString();
                                        fuzzy_reg_names.Add(dict_set_to_set[set.UserFriendlyName] + no_underscore_indexed_reg); // Starts with corrected set name but is indexed without underscore
                                        fuzzy_reg_names.Add(dict_set_to_set[set.UserFriendlyName] + indexed_name_m.Groups[1].ToString());// Starts with corrected set name but is not indexed
                                    }
                                }

                                foreach(var fuzzy_reg in fuzzy_reg_names)
                                {
                                    if(subregisters.ContainsKey(fuzzy_reg))
                                    {
                                        if (subregisters[fuzzy_reg] != null)
                                            register.SubRegisters = subregisters[fuzzy_reg].ToArray();
                                        used_subregisters.Add(fuzzy_reg);// Still subregisters found, just set to null

                                        continue;// Register found!!
                                    }
                                }
                            }
                        }
                    }

                    sets.Add(set);
                }

                peripherals.Add(subfamily.Name, sets.ToArray());

                // Verify that all parsed types have been used
                if(used_types.Count != registerset_types.Count)
                {
                    foreach(var type in registerset_types.Keys)
                    {
                        if (!used_types.Contains(type) && !nested_types.Values.Contains(type))
                            Console.WriteLine("Unused hardware register set type: " + type + " in " + subfamily.Name);
                    }
                }

                // Verify that all parsed subregister lists have been used
                if(used_subregisters.Count != subregisters.Count)
                {
                    int unused_subregs = 0;
                    foreach (var reg_abbr in subregisters.Keys)
                    {
                        if (!used_subregisters.Contains(reg_abbr) && (subregisters[reg_abbr] != null) && !nonexisting_regs.Contains(reg_abbr))
                        {
                            Console.WriteLine("Unused subregisters for : " + reg_abbr + " in " + subfamily.Name);
                            unused_subregs++;
                        }
                    }
                    Console.WriteLine((100.0*(float)(subregisters.Count - unused_subregs) / (float)subregisters.Count).ToString() + "%, " + subregisters.Count + " of parsed subregisters used. Unused " + unused_subregs.ToString() + " in " + subfamily.Name + ".");
                }
            }

            return peripherals;
    
        }

        private static Family ProcessRegisterSetAddresses(string addressesFile)
        {
            Family family = new Family()
            {
                Name = Path.GetFileNameWithoutExtension(addressesFile),
                SetBaseAddresses = new List<Peripheral>(),
                Defines = new Dictionary<string, int>() { { "CHIP_" + Path.GetFileNameWithoutExtension(addressesFile).ToUpper(), 1} },
                HeadersToIgnore = new List<string>(),
                AdditionalStructDependencies = new List<string>(),
                TypePreferences =new Dictionary<string,TypePreference>()
            };
            
            foreach (var line in File.ReadLines(addressesFile))
            {
                if (line.StartsWith("//"))
                    continue;

                // Definitions
                if (line.Contains('='))
                {
                    string[] vals = line.Split(new char[] { ',' });
                    foreach(var val in vals)
                    {
                        string[] split = val.Split(new char[] { '=' });
                        family.Defines.Add(split[0], Int32.Parse(split[1]));
                    }
                }
                // Include files to exclude from parsing
                else if(line.Contains('!'))
                {
                    string[] vals = line.Split(new char[] { ',' });
                    foreach (var val in vals)
                    {
                        family.HeadersToIgnore.Add(val.Replace("!",""));
                    }
                }
                // Additional structs to parse
                else if(line.Contains('+'))
                {
                    string[] vals = line.Split(new char[] { ',' });
                    foreach (var val in vals)
                    {
                        family.AdditionalStructDependencies.Add(val.Replace("+", ""));
                    }
                }
                // Type preferences to parse
                else if (line.Contains(')'))
                {
                    string[] vals = line.Split(new char[] { ')' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var val in vals)
                    {
                        string[] split = val.Replace("(","").Split(new char[] { ',' });
                        family.TypePreferences.Add(split[1], new TypePreference() { FileName = split[0], Type = split[1], FirstOccurence = (split[2] == "1")});
                    }
                }
                else//Set addresses
                {
                    string[] vals = line.Split(new char[] { ',' });
                    if (vals.Length != 3)
                        throw new Exception("Problems reading addresses csv file!");

                    string set_name = vals[0];
                    string set_type = vals[1];
                    ulong address = ParseHex(vals[2]);

                    family.SetBaseAddresses.Add(new Peripheral {Name = set_name, Type = set_type, Address = address});
                }
            }

            return family;
        }

        private static Dictionary<string, HardwareRegisterSet> ProcessRegisterSetTypes(string headerDir, Family family, out Dictionary<string, string> nested_types)
        {
            List<string> FILES_WITHOUT_STRUCTS = new List<string>() { "chip.h", "cmsis.h",
                "clock_11xx.h", "ccand_11xx.h",
                "system_LPC12xx.h",
                "clock_13xx.h", "flash_13xx.h", "i2cm_13xx.h", "sys_config.h",
                "clock_15xx.h", "i2cm_15xx.h", "i2cs_15xx.h", "rom_adc_15xx.h", "rom_can_15xx.h", "rom_dma_15xx.h", "rom_i2c_15xx.h", "rom_pwr_15xx.h", "rom_spi_15xx.h", "rom_uart_15xx.h", "sct_pwm_15xx.h",
                "chip_lpc175x_6x.h", "chip_lpc177x_8x.h", "chip_lpc407x_8x.h", "clock_17xx_40xx.h", "sdmmc_17xx_40xx.h", "spifi_17xx_40xx.h",
                "aes_18xx_43xx.h", "chip_clocks.h", "chip_lpc18xx.h", "chip_lpc43xx.h", "clock_18xx_43xx.h", "i2cm_18xx_43xx.h", "i2c_18xx_43xx.h", "iap_18xx_43xx.h", "sct_pwm_18xx_43xx.h", "sdmmc_18xx_43xx.h",
                "clock_8xx.h", "error_8xx.h", "i2c_8xx.h", "romapi_8xx.h", "rom_i2c_8xx.h", "rom_pwr_8xx.h", "rom_uart_8xx.h"
            };

            Dictionary<string, HardwareRegisterSet> types = new Dictionary<string, HardwareRegisterSet>();
            Dictionary<string, HardwareRegisterSet> other_types = new Dictionary<string, HardwareRegisterSet>();
            nested_types = new Dictionary<string, string>();

            Dictionary<string, ulong> dict_type_sizes = new Dictionary<string, ulong>();
            foreach(var standard_type in STANDARD_TYPE_SIZES.Keys)
            {
                dict_type_sizes.Add(standard_type, STANDARD_TYPE_SIZES[standard_type]);
            }

            foreach(var header in new DirectoryInfo(headerDir).GetFiles("*.h"))
            {
                if (family.HeadersToIgnore.Contains(header.Name))
                    continue;

                string file = File.ReadAllText(header.FullName);

                Regex struct_regex = new Regex(@"typedef struct(.+?)}[ ]*([a-zA-Z0-9_]+);", RegexOptions.Singleline);
                var structs_m = struct_regex.Matches(file);

                if ((structs_m.Count == 0) && !FILES_WITHOUT_STRUCTS.Contains(header.Name))
                {
                    // throw new Exception("No structs found in header file!");
                    Console.WriteLine("No structs found in header file!" + header.Name);
                    continue;

                }

                foreach (Match struct_m in structs_m)
                {
                    string struct_name = struct_m.Groups[2].ToString();
                    Regex struct_name_regex = new Regex(@"(LPC_(.+?)_T)(ypeDef)?");
                    bool is_main_struct = struct_name_regex.IsMatch(struct_name);
                    if (!is_main_struct && !family.AdditionalStructDependencies.Contains(struct_name))
                        continue;//This is not a struct we need for peripheral registers

                    string struct_contents = struct_m.Groups[1].ToString();

                    ulong struct_size;
                    HardwareRegisterSet set = new HardwareRegisterSet()
                    {
                        UserFriendlyName = struct_name,
                        // 3. Add the registers to the register set
                        Registers = ProcessStructContents(header.Name, family, ref other_types, dict_type_sizes, struct_name, struct_contents, false, out struct_size, ref nested_types)
                    };

                    if (set.Registers.Length == 0)
                        throw new Exception("Failed to parse any of the struct's registers!");

                    try
                    {
                        if (is_main_struct)
                            types.Add(struct_name, set);
                        else
                            other_types.Add(struct_name, set);
                        dict_type_sizes[struct_name] = struct_size;
                    } catch(ArgumentException ex)
                    {
                        if (family.TypePreferences.ContainsKey(struct_name) &&
                            (family.TypePreferences[struct_name].FileName == header.Name) )
                        {
                            if(!family.TypePreferences[struct_name].FirstOccurence)
                                types[struct_name] = set;
                        }
                        else
                            Console.WriteLine("Duplicate set:   " + set.UserFriendlyName + " in " + header.Name);
                    }
                }
            }

            // Insert the nested registers
            foreach (var struct_type in other_types)
            {
                List<HardwareRegister> registers = new List<HardwareRegister>(struct_type.Value.Registers);

                for (int i = 0; i < registers.Count; i++)
                {
                    var register = registers[i];

                    string key = struct_type.Key + "+" + register.Name;

                    if (nested_types.ContainsKey(key))
                    {
                        string reg_name = register.Name;
                        HardwareRegister[] registers2 = null;
                        if (other_types.ContainsKey(nested_types[key]))
                            registers2 = other_types[nested_types[key]].Registers;

                        registers.Remove(register);

                        foreach (var register2 in registers2)
                        {
                            HardwareRegister register2_cpy = DeepCopy(register2);

                            string hex_offset2 = register2_cpy.Address;
                            if (!string.IsNullOrEmpty(register.Address) && !string.IsNullOrEmpty(hex_offset2))
                            {
                                ulong offset = ParseHex(register.Address);
                                ulong offset2 = ParseHex(hex_offset2);
                                register2_cpy.Address = FormatToHex((offset + offset2));
                            }

                            register2_cpy.Name = reg_name + "_" + register2_cpy.Name; // Make nested name to collapse the hierarchy

                            registers.Insert(i, register2_cpy);
                            i++;
                        }

                        i--;
                    }
                }

                struct_type.Value.Registers = registers.ToArray();
            }

            foreach(var struct_type in types)
            {
                List<HardwareRegister> registers = new List<HardwareRegister>(struct_type.Value.Registers);

                for (int i = 0; i < registers.Count; i++)
                {
                    var register = registers[i];

                    string key = struct_type.Key + "+" + register.Name;

                    if (nested_types.ContainsKey(key))
                    {
                        string reg_name = register.Name;
                        HardwareRegister[] registers2 = null;
                        if(types.ContainsKey(nested_types[key]))
                            registers2  = types[nested_types[key]].Registers;
                        else if (other_types.ContainsKey(nested_types[key]))
                            registers2  = other_types[nested_types[key]].Registers;

                        registers.Remove(register);

                        foreach (var register2 in registers2)
                        {
                            HardwareRegister register2_cpy = DeepCopy(register2);

                            string hex_offset2 = register2_cpy.Address;
                            if (!string.IsNullOrEmpty(register.Address) && !string.IsNullOrEmpty(hex_offset2))
                            {
                                ulong offset = ParseHex(register.Address);
                                ulong offset2 = ParseHex(hex_offset2);
                                register2_cpy.Address = FormatToHex((offset + offset2));
                            }

                            register2_cpy.Name = reg_name + "_" + register2_cpy.Name; // Make nested name to collapse the hierarchy

                            registers.Insert(i, register2_cpy);
                            i++;
                        }

                        i--;
                    }
                }

                struct_type.Value.Registers = registers.ToArray();
            }

            return types;
        }

        class HardwareSubRegisterComparer : IEqualityComparer<HardwareSubRegister>
        {
            public bool Equals(HardwareSubRegister x, HardwareSubRegister y)
            {
                return x.Name.Equals(y.Name, StringComparison.InvariantCultureIgnoreCase) && (x.FirstBit == y.FirstBit) && (x.SizeInBits == y.SizeInBits);
            }

            public int GetHashCode(HardwareSubRegister obj)
            {
                return obj.Name.ToUpperInvariant().GetHashCode() ^ obj.FirstBit.GetHashCode() ^ obj.SizeInBits.GetHashCode();
            }
        }

        private static Dictionary<string, List<HardwareSubRegister>> ProcessSubregisters(string familyDirectory, Family subfamily)
        {
            Dictionary<KeyValuePair<string, string>, List<HardwareSubRegister>> subs = new Dictionary<KeyValuePair<string, string>, List<HardwareSubRegister>>();

            string file = File.ReadAllText(Path.Combine(familyDirectory, subfamily.Name + ".um"));

            Regex reg_desc_regex = new Regex(@"([0-9]+).([0-9]+)(\.[0-9]+)?[ ]+Register description", RegexOptions.Multiline);
            if (!reg_desc_regex.IsMatch(file))
                throw new Exception("No register descriptions found in the user manual!");

            Regex page_end_regex = new Regex(@"[-]+ Page [0-9]+[-]+");

            Regex tables_regex = new Regex(@"[0-9]+.[0-9]+ Tables");// Do not parse the index at the end of the file
            int file_end_index = tables_regex.Match(file).Index;

            int subregisters = 0, tables_found = 0;
            int file_index = 0, last_chapter = 0;
            Match reg_desc_m;
            while ((reg_desc_m = reg_desc_regex.Match(file, file_index, file_end_index - file_index)).Success)
            {
                file_index = reg_desc_m.Index + reg_desc_m.Length;

                int reg_chapter = Int32.Parse(reg_desc_m.Groups[1].ToString());
                int reg_section = Int32.Parse(reg_desc_m.Groups[2].ToString());

                if (last_chapter > reg_chapter)
                    break;// Stumbled upon the index or changes
                last_chapter = reg_chapter;

                Regex subreg_desc_regex = new Regex(@"^[ ]*" + reg_chapter + @"\.[" + reg_section + @"-9]+[\.]?[0-9]*[\.]?[0-9]*[\.]?[0-9]*[ ]+([^\r\n]+)\r", RegexOptions.Multiline);

                Match subreg_desc_m;
                while ((subreg_desc_m = subreg_desc_regex.Match(file, file_index, file_end_index - file_index)).Success && (subreg_desc_m.Index + subreg_desc_m.Length < reg_desc_m.NextMatch().Index))
                {
                    subregisters++;

                    file_index = subreg_desc_m.Index + subreg_desc_m.Length;
                    bool table_found = false;

                    string reg_name;
                    List<KeyValuePair<string, string>> regs;

                    Regex next_subchapter_regex = new Regex(@"^[ ]*" + (reg_chapter + 1) + @"\.1[\.]?[0-9]*[\.]?[0-9]*[\.]?[0-9]*[ ]+([^\r\n]+)\r", RegexOptions.Multiline);
                    Match next_subchapter_m = next_subchapter_regex.Match(file, file_index);
                    int next_subchapter_match_index = next_subchapter_m.Success ? next_subchapter_m.Index : (file.Length - 1);
                    int next_subreg_match_index = subreg_desc_m.NextMatch().Success? subreg_desc_m.NextMatch().Index : (file.Length - 1);
                    int next_reg_match_index = reg_desc_m.NextMatch().Success? reg_desc_m.NextMatch().Index : (file.Length-1);

                    int end_index = next_subchapter_match_index;
                    if (next_subreg_match_index < end_index)
                        end_index = next_subreg_match_index;
                    if ((next_reg_match_index < end_index) && (next_reg_match_index > file_index))
                        end_index = next_reg_match_index;

                    int found_end_index = end_index;
                    while (FindTableHeader(file, ref file_index, ref found_end_index, out reg_name, out regs))
                    {
                        //Fix I2C naming
                        reg_name = reg_name.Replace(" I  C", " I2C");
                        if(reg_name.StartsWith("I  C"))
                            reg_name = reg_name.Replace("I  C", "I2C");
                        //Remove line ends
                        reg_name = reg_name.Replace("\n", "");
                        reg_name = reg_name.Replace("\r", "");
                        //Remove ...continued
                        reg_name = reg_name.Replace("…continued", "");
                        //Remove excessive spaces
                        while (reg_name.Contains("  "))
                        {
                            reg_name = reg_name.Replace("  ", " ");
                        }
                        //Remove bit description
                        reg_name = reg_name.Replace("bit description", "");
                        //Trim
                        reg_name = reg_name.Trim();

                        if (!table_found)
                            tables_found++;
                        table_found = true;

                        if ((regs.Count != 0))
                        {
                            // Reduce the prospective table size even more
                            found_end_index = page_end_regex.Match(file, file_index, found_end_index - file_index).Success ? Math.Min(found_end_index, page_end_regex.Match(file, file_index, found_end_index - file_index).Index) : found_end_index;
                            
                            List<HardwareSubRegister> reg_subs = ProcessTableRows(file, ref file_index, found_end_index, ProcessTableHeaderRow(file, ref file_index, found_end_index));
                            // Add the subregisters to all the registers they belong to
                            foreach (var reg in regs)
                            {
                                KeyValuePair<string, string> key = new KeyValuePair<string, string>(reg.Key, reg_name);
                                if (!subs.ContainsKey(key) || (subs.ContainsKey(key) && (subs[key] == null)))
                                    subs[key] = reg_subs;
                                else if (reg_subs != null)
                                    subs[key].AddRange(reg_subs);// If the table continues on another page then the subregisters are split between two tables, merge the subregisters here
                            }
                        }
                        else if(!reg_name.Contains("allocation"))
                        {
                            Console.WriteLine("Register name not parsed in " + subfamily.Name + " for " + reg_name);
                        }
                        found_end_index = end_index;
                    }

                    Regex table_def_regex = new Regex(@"^[ ]*Table [0-9]+[\.:][ ]{2,}", RegexOptions.Multiline);
                    if (!table_found && table_def_regex.Match(file, file_index, Math.Max(end_index - file_index, 0)).Success && (subreg_desc_m.Groups[0].ToString().Contains("register") || subreg_desc_m.Groups[0].ToString().Contains("Register"))
                        && !subreg_desc_m.Groups[0].ToString().Contains("status") && !subreg_desc_m.Groups[0].ToString().Contains("Message")
                        && !subreg_desc_m.Groups[0].ToString().Contains("summary") && !subreg_desc_m.Groups[0].ToString().Contains("group") && !subreg_desc_m.Groups[0].ToString().Contains("description") && !subreg_desc_m.Groups[0].ToString().Contains("registers") && !subreg_desc_m.Groups[0].ToString().Contains("Registers") && !subreg_desc_m.Groups[0].ToString().Contains("set-up"))
                        Console.WriteLine(subfamily.Name + ": " + subreg_desc_m.Groups[0].ToString().Trim());
                }
            }

            // Post-process and check the subregisters for problems
            foreach (var kv in subs)
            {
                var v = kv.Value;
                if ((v != null) && (v.Count > 1))
                {
                    // Sort the subregisters based on first bit
                    v.Sort((x, y) => { return (x.FirstBit - y.FirstBit); });

                    // Remove repetitions
                    var tmp = v.Distinct(new HardwareSubRegisterComparer()).ToList();
                    v.Clear();
                    v.AddRange(tmp);

                    // Check the subregisters for any overlap in ranges
                    int index = -1;
                    foreach (var subreg in v)
                    {
                        if (subreg.FirstBit < index)
                            Console.WriteLine("Overlap in subregister ranges for register " + kv.Key.Value + kv.Key.Key + " and subregister " + subreg.Name);
                        index = subreg.FirstBit + subreg.SizeInBits;
                    }
                }
            }

            Dictionary<string, List<HardwareSubRegister>> prepared_subs = new Dictionary<string, List<HardwareSubRegister>>();
            foreach(var subkey in subs.Keys)
            {
                if (!prepared_subs.ContainsKey(subkey.Key))
                    prepared_subs.Add(subkey.Key, subs[subkey]);
                else
                {
                    // Remove duplicates
                    // Only add indexed register if the subregisters are different from non-indexed or previously indexed register's subregisters
                    bool equal = CompareSubregisterLists(prepared_subs[subkey.Key], subs[subkey]);

                    if (!equal)
                    {
                        int index = 2;
                        while (true)
                        {
                            string indexed_duplicate_subreg_abbr = subkey.Key + "{" + index + "}";
                            if (!prepared_subs.ContainsKey(indexed_duplicate_subreg_abbr))
                            {
                                prepared_subs.Add(indexed_duplicate_subreg_abbr, subs[subkey]);
                                break;
                            }
                            else if (CompareSubregisterLists(prepared_subs[indexed_duplicate_subreg_abbr], subs[subkey]))
                            {
                                break;// Indexed register with identical subregisters found
                            }
                            index++;
                        }
                    }
                }
            }

            return prepared_subs;
        }

        private static bool CompareSubregisterLists(List<HardwareSubRegister> list1, List<HardwareSubRegister> list2)
        {
            if ((list1 == null) && (list2 == null))
                return true;
            if ((list1 == null) && (list2 != null))
                return false;
            if ((list2 == null) && (list1 != null))
                return false;
            if (list1.Count != list2.Count)
                return false;

            // Assume the list has already been sorted by the indexes using the same sorting algorithm
            // Only compare properties that are known to be used, i.e. not parentregister or knownvalues
            for(int i=0; i< list1.Count; i++)
            {
                if (list1[i].FirstBit != list2[i].FirstBit)
                    return false;
                if (list1[i].SizeInBits != list2[i].SizeInBits)
                    return false;
                if (!list1[i].Name.Equals(list2[i].Name, StringComparison.InvariantCultureIgnoreCase))
                    return false;
            }

            return true;
        }

        private class TableNameProcesser
        {
            public delegate void ProcessTableHeaderMatchHandler(Match m, ref string reg_name, ref List<KeyValuePair<string, string>> registers);

            public string RegexPattern;
            public ProcessTableHeaderMatchHandler ProcessTableHeaderMatch;

            public static string GetAddress(Match m, int index)
            {
                return m.Groups[index].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
            }

            public static string GetName(Match m, int index)
            {
                return m.Groups[index].ToString().Trim();
            }

            public static int GetNumber(Match m, int index)
            {
                return Int32.Parse(m.Groups[index].ToString());
            }
        }

        private static bool FindTableHeader(string file, ref int start_index, ref int end_index, out string reg_name, out List<KeyValuePair<string, string>> registers)
        {
            registers = new List<KeyValuePair<string, string>>();
            reg_name = null;

            string table_beginning = @"[\r]?\n[ ]*Table [0-9]+[\.:][ ]{2,}";
            string register_long_name = @"([^\r\n]+) [rR]egister";
            string register_abbr = @"([A-Z0-9_]+)";
            string register_addr = @"([0-9A-Fa-fx]{4,6}[ \n\r]*[0-9A-Fa-f]{4,5})";
            string regset_abbr = @"([A-Z0-9_ \n\r]+)";
            string space = @"[ \n\r]+";
            string dashcoloncomma = @"[, \-:]+";

            string bit_desc = @"[ \n\r]*bit" + space + "description";

            List<TableNameProcesser> processors = new List<TableNameProcesser>(){
                #region Processors
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]? \(" + register_abbr + @"([0-9])\/([0-9])" + dashcoloncomma + @"(.*?)" + space + "and" + space + register_abbr + @"([0-9])\/([0-9])(.*?)\)" + bit_desc,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr1 = TableNameProcesser.GetName(m, 2);
                        int reg_abbr1_index1 = TableNameProcesser.GetNumber(m,3);
                        int reg_abbr1_index2 = TableNameProcesser.GetNumber(m, 4);
                        string reg_abbr2 = TableNameProcesser.GetName(m, 6);
                        int reg_abbr2_index1 = TableNameProcesser.GetNumber(m,7);
                        int reg_abbr2_index2 = TableNameProcesser.GetNumber(m, 8);

                        regs.Add(new KeyValuePair<string, string>(reg_abbr1 + reg_abbr1_index1.ToString(), regname));
                        regs.Add(new KeyValuePair<string, string>(reg_abbr1 + reg_abbr1_index2.ToString(), regname));
                        regs.Add(new KeyValuePair<string, string>(reg_abbr2 + reg_abbr2_index1.ToString(), regname));
                        regs.Add(new KeyValuePair<string, string>(reg_abbr2 + reg_abbr2_index2.ToString(), regname));
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]?[ 0-9]+\((" + register_abbr + @"[, \-]+(address)?[es]{0,2}[ \n\r]+" + register_addr + @"([ to;and\n\r]+" + register_abbr + @"[,\- \n\r]+(address)?[ \n\r]*" + register_addr + @")?[^\)]*)[\)]?[\)]?[ \n\r]+bit[ \n\r]+description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = m.Groups[3].ToString().Trim();
                        string reg_addr = m.Groups[5].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        string reg_abbr2 = m.Groups[7].ToString().Trim();
                        string reg_addr2 = m.Groups[9].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                        if(reg_abbr2 != "")
                            regs.Add(new KeyValuePair<string, string>(reg_abbr2, regname));
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @" bit allocation([^\n\r]*)",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = TableNameProcesser.GetName(m, 1);

                        string derived_reg_abbr = null;
                        if (!regname.Contains(" "))
                            derived_reg_abbr = regname;

                        regname += TableNameProcesser.GetName(m, 2).Replace(" I  C", " I2C");

                        regs.Add(new KeyValuePair<string, string>(derived_reg_abbr, regname));
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"s \(" + register_abbr + @" to ([0-9]+), addresses " + register_addr + @" to ([0-9]+) and[ \n\r]+" + register_abbr + @" to ([0-9]+), addresses " + register_addr + @" to ([0-9]+)\) bit description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = m.Groups[2].ToString().Trim();
                        int reg_abbr_to = Int32.Parse(m.Groups[3].ToString());
                        string reg_addr = m.Groups[4].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        int reg_addr_to = Int32.Parse(m.Groups[5].ToString());
                        string reg_abbr2 = m.Groups[6].ToString().Trim();
                        int reg_abbr2_to = Int32.Parse(m.Groups[7].ToString());
                        string reg_addr2 = m.Groups[8].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        int reg_addr2_to = Int32.Parse(m.Groups[9].ToString());

                        int start_i = Int32.Parse(reg_abbr.Substring(reg_abbr.Length - 1));
                        for (int i = start_i; i <= reg_abbr_to; i++)
                        {
                            regs.Add(new KeyValuePair<string,string>(
                                reg_abbr.Substring(0, reg_abbr.Length-1) + i.ToString(),
                                regname));
                        }

                        start_i = Int32.Parse(reg_abbr2.Substring(reg_abbr2.Length - 1));
                        for (int i = Int32.Parse(reg_abbr2.Substring(reg_abbr2.Length - 1)); i <= reg_abbr2_to; i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr2.Substring(0, reg_abbr2.Length - 1) + i.ToString(),
                                regname));
                        }
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"s \(" + register_abbr + @" to ([0-9]+), addresses " + register_addr + @" to ([0-9]+) \(" + register_abbr + @"\) and[ \n\r]+" + register_addr + @" to ([0-9]+) \(" + register_abbr + @"\)\) bit description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();

                        string reg_abbr = m.Groups[2].ToString().Trim();
                        int reg_abbr_to = Int32.Parse(m.Groups[3].ToString());
                        string reg_addr = m.Groups[4].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        int reg_addr_to = Int32.Parse(m.Groups[5].ToString());
                        string reg_set = m.Groups[6].ToString();

                        string reg_addr2 = m.Groups[7].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        int reg_addr2_to = Int32.Parse(m.Groups[8].ToString());
                        string reg_set2 = m.Groups[9].ToString();

                        int start_i = Int32.Parse(reg_abbr.Substring(reg_abbr.Length - 1));
                        for (int i = start_i; i <= reg_abbr_to; i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr.Substring(0, reg_abbr.Length - 1) + i.ToString(),
                                regname));
                        }

                        start_i = Int32.Parse(reg_abbr.Substring(reg_abbr.Length - 1));
                        for (int i = Int32.Parse(reg_abbr.Substring(reg_abbr.Length - 1)); i <= reg_abbr_to; i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr.Substring(0, reg_abbr.Length - 1) + i.ToString(),
                                regname));
                        }
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"s \(" + register_abbr + @"\[([0-9]+):([0-9]+)\], addresses " + register_addr + @" \(" + register_abbr + @"\) to " + register_addr + @" \(" + register_abbr + @"\)[ \n\r]+\(" + register_abbr + @"\), " + register_addr + @" \(" + register_abbr + @"\) to "+ register_addr + @" \(" + register_abbr + @"\) \("+ register_abbr + @"\)\) bit description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();

                        string reg_abbr = m.Groups[2].ToString().Trim();
                        int reg_abbr_from = Int32.Parse(m.Groups[3].ToString());
                        int reg_abbr_to = Int32.Parse(m.Groups[4].ToString());
                        string reg_addr_from = m.Groups[5].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        string reg_addr_to = m.Groups[7].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        string reg_set = m.Groups[9].ToString();

                        //string reg_addr2_from = m.Groups[10].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        //string reg_addr2_to = m.Groups[12].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        //string reg_set2 = m.Groups[14].ToString();

                        for (int i = Math.Min(reg_abbr_from, reg_abbr_to); i <= Math.Max(reg_abbr_from, reg_abbr_to); i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr + i.ToString(),
                                regname));
                        }

                        //for (int i = reg_abbr_from; i <= reg_abbr_to; i++)
                        //{
                        //    regs.Add(new KeyValuePair<string, string>(
                        //        reg_abbr + i.ToString(),
                        //        regname));
                        //}
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]?" + space + @"\(" + register_abbr + @" to ([0-9]+)" + dashcoloncomma + @"(.*?)\)" + bit_desc,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();

                        string reg_abbr_from = m.Groups[2].ToString().Trim();
                        int reg_abbr_index_to = TableNameProcesser.GetNumber(m, 3);

                        Regex index_regex = new Regex(@"^(.*?)([0-9]+)$");
                        Match index_m = index_regex.Match(reg_abbr_from);
                        string reg_abbr = TableNameProcesser.GetName(index_m, 1);
                        int reg_abbr_index_from = TableNameProcesser.GetNumber(index_m, 2);

                        for (int i = reg_abbr_index_from; i <= reg_abbr_index_to; i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr + i.ToString(),
                                regname));
                        }
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]?" + space + @"\(" + register_abbr + @" to " + register_abbr + @"[, -]+addresses " + register_addr + @" to[ \n\r]+" + register_addr + @"\)" + space + "bit" + space + "description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();

                        string reg_abbr_from = m.Groups[2].ToString().Trim();
                        string reg_abbr_to = m.Groups[3].ToString().Trim();
                        //string reg_addr_from = m.Groups[4].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");
                        //string reg_addr_to = m.Groups[5].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");

                        Regex index_regex = new Regex(@"^(.*?)([0-9]+)([a-zA-Z_]*)$");
                        Match index_m = index_regex.Match(reg_abbr_from);
                        string reg_abbr = TableNameProcesser.GetName(index_m, 1);
                        int reg_abbr_index_from = TableNameProcesser.GetNumber(index_m, 2);
                        string reg_abbr_ctn = TableNameProcesser.GetName(index_m, 3);

                        index_m = index_regex.Match(reg_abbr_to);
                        if (TableNameProcesser.GetName(index_m, 1) != reg_abbr || TableNameProcesser.GetName(index_m, 3) != reg_abbr_ctn)
                            throw new Exception("Indexed register name is not the same!");
                        int reg_abbr_index_to = TableNameProcesser.GetNumber(index_m, 2);

                        for (int i = reg_abbr_index_from; i <= reg_abbr_index_to; i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr + i.ToString() + reg_abbr_ctn,
                                regname));
                        }
                    }},
                    //new TableNameProcesser(){
                    //RegexPattern = table_beginning + register_long_name + @" bit description \(" + register_abbr + @", address: " + register_addr + @"\)",
                    //ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    //{
                    //    regname = m.Groups[1].ToString().Trim();
                    //    string reg_abbr = m.Groups[2].ToString().Trim();
                    //    string reg_addr = m.Groups[3].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");

                    //    regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    //}},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]?([^\r\n]+)?" + space + @"\(([A-Za-z0-9_]+)[, -]+(address)?[:]?[RW\- ]* ([0-9A-Fa-fx]{6}[ \n\r]*[0-9A-Fa-fx]{4})[^\n\r]*[\n\r ]*\)(" + space + @"bit" + space + @"description|" + space + @"bit" + space + @"allocation)?",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = m.Groups[3].ToString().Trim();
                        string reg_addr = m.Groups[5].ToString().Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "");

                        if(!m.Groups[0].ToString().Contains("allocation"))
                            regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"s ([0-9]+) to ([0-9]+) \(" + register_abbr + @" [to-]+[ \n\r]+" + register_abbr + dashcoloncomma + @"address[es]* " + register_addr + space + @"to" + space + register_addr + @"\) bit description" ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        int reg_index_from = TableNameProcesser.GetNumber(m,2);
                        int reg_index_to = TableNameProcesser.GetNumber(m, 3);
                        string reg_abbr_from = TableNameProcesser.GetName(m, 4);
                        string reg_abbr_to = TableNameProcesser.GetName(m, 5);
                        string reg_addr_from = TableNameProcesser.GetAddress(m, 6);
                        string reg_addr_to = TableNameProcesser.GetAddress(m, 7);

                        for(int i=reg_index_from;i<=reg_index_to;i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr_from.Substring(0, reg_abbr_from.Length - 1) + i.ToString(),
                                regname
                                ));
                        }
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]? \(" + register_abbr + @"[, -]+(address[es]{0,2} )?" + register_addr + @"( \(" + regset_abbr + @"\))?[, and]+[ \n\r]+(" + register_abbr + @",[ \n\r]+address[ \n\r]+)?" + @"[and ]{0,5}" + register_addr + @"([ \n\r]+\(" + regset_abbr + @"\))?([,;][ \n\r]+" + register_addr + @" \(" + regset_abbr + @"\))?\)[ \n\r]+bit[ \n\r]+description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 2);
                        string reg_addr1 = TableNameProcesser.GetAddress(m, 4);
                        string reg_addr2 = TableNameProcesser.GetAddress(m, 9);

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));

                        if(m.Groups[12].ToString() != "")
                        {
                            string reg_addr3 = TableNameProcesser.GetAddress(m, 13);
                            regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                        }
                    }},
                    //new TableNameProcesser(){
                    //RegexPattern = table_beginning + register_long_name + @"[s]? \(" + register_abbr + @" to ([0-9]+), address " + register_addr + @" to " + register_addr + @"\)" + bit_desc,
                    //ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    //{
                    //    regname = m.Groups[1].ToString().Trim();
                    //    string reg_abbr = TableNameProcesser.GetName(m, 2);
                    //    int reg_abbr_to = TableNameProcesser.GetNumber(m, 3);
                    //    string reg_addr_from = TableNameProcesser.GetAddress(m, 4);
                    //    string reg_addr_to = TableNameProcesser.GetAddress(m, 5);

                    //    int reg_abbr_from = Int32.Parse(reg_abbr.Substring(reg_abbr.Length-1));
                    //    for (int i = reg_abbr_from; i <= reg_abbr_to; i++)
                    //    {
                    //        regs.Add(new KeyValuePair<string, string>(
                    //            reg_abbr.Substring(0, reg_abbr.Length - 1) + i.ToString(),
                    //            regname
                    //            ));
                    //    }
                    //}},
                    new TableNameProcesser(){//IOCON Type registers only
                    RegexPattern = table_beginning + "Type ([A-Z]) " + register_long_name + @"[s]?" + bit_desc,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[2].ToString().Trim();
                        string reg_abbr = m.Groups[1].ToString();

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]?[ ]+bit description",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();

                        string reg_abbr = null;
                        if (regname == "DMA Request Select")
                            reg_abbr = "DMACReqSel";
                        else if (regname == "DMA Software Single Request")
                            reg_abbr = "DMACSoftSReq";
                        if(reg_abbr != null)
                            regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){
                    RegexPattern = table_beginning + register_long_name + @"[s]? \(" + register_abbr + @"\[([0-9a-fA-F]+), ([0-9a-fA-F]+), ([0-9a-fA-F]+)[,]?[ ]?([0-9a-fA-F]+)?\]" + dashcoloncomma +@"([x0-9 ]{9})\[([0-9a-fA-F]+), ([0-9a-fA-F]+), ([0-9a-fA-F]+)[,]?[ ]?([0-9a-fA-F]+)?\]\)" + space + @"bit" + space + @"description" ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 2);

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + register_long_name + @"[s]?([^\n]*)\(" + register_abbr + @"\[([0-9]+)\:[B]?([0-9]+)\]" + register_abbr + @"?" + dashcoloncomma + @"(.*?)\)" + bit_desc ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 3);
                        int reg_abbr_index = TableNameProcesser.GetNumber(m, 4);
                        int reg_abbr_index2 = TableNameProcesser.GetNumber(m, 5);
                        string reg_abbr_continued = TableNameProcesser.GetName(m, 6);

                        for (int i = Math.Min(reg_abbr_index, reg_abbr_index2); i <= Math.Max(reg_abbr_index, reg_abbr_index2); i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(reg_abbr + i.ToString() + reg_abbr_continued, regname));
                        }
                    }},            
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + register_long_name + @"[0-9]?[s]? ([0-9]+) to ([0-9]+) \(([A-Z0-9_\/\[\]]+)" + dashcoloncomma + @"(.*?)\)" + bit_desc ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 4);
                        int index1 = TableNameProcesser.GetNumber(m, 2);
                        int index2 = TableNameProcesser.GetNumber(m, 3);

                        for (int i = Math.Min(index1, index2); i <= Math.Max(index1, index2);i++ )
                            regs.Add(new KeyValuePair<string, string>(reg_abbr + i, regname));
                    }},
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + register_long_name + space + register_abbr + space + "to" + space + register_abbr + space + @"(.*?)\)" + bit_desc,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr_from = TableNameProcesser.GetName(m, 2);
                        string reg_abbr_to = TableNameProcesser.GetName(m, 3);

                        Regex index_regex = new Regex(@"^(.*?)([0-9]+)([a-zA-Z]*)$");
                        Match index_m = index_regex.Match(reg_abbr_from);

                        string reg_abbr = TableNameProcesser.GetName(index_m, 1);
                        int reg_abbr_index_from = TableNameProcesser.GetNumber(index_m, 2);
                        string reg_abbr_ctn = TableNameProcesser.GetName(index_m, 3);

                        index_m = index_regex.Match(reg_abbr_to);
                        if (TableNameProcesser.GetName(index_m, 1) != reg_abbr || TableNameProcesser.GetName(index_m, 3) != reg_abbr_ctn)
                            throw new Exception("Indexed register name is not the same!");
                        int reg_abbr_index_to = TableNameProcesser.GetNumber(index_m, 2);

                        for (int i = reg_abbr_index_from; i <= reg_abbr_index_to; i++)
                        {
                            regs.Add(new KeyValuePair<string, string>(
                                reg_abbr + i.ToString() + reg_abbr_ctn,
                                regname));
                        }
                    }},
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + register_long_name + @"[s]?" + space + @"(.*?)\(" + register_abbr + @"\[([0-9]+)\/([0-9]+)\/([0-9]+)[\/]?([0-9]+)?\]" + register_abbr + @"?" + dashcoloncomma + @"(.*?)(([A-Z0-9_\/]{3,})\[([0-9]+)\/([0-9]+)\/([0-9]+)\](.*?))?\)" + bit_desc ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = TableNameProcesser.GetName(m, 1).Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 3);
                        int reg_abbr_index = TableNameProcesser.GetNumber(m, 4);
                        int reg_abbr_index2 = TableNameProcesser.GetNumber(m, 5);
                        int reg_abbr_index3 = TableNameProcesser.GetNumber(m, 6);
                        string reg_abbr_continued = TableNameProcesser.GetName(m, 8);

                        regs.Add(new KeyValuePair<string, string>(reg_abbr + reg_abbr_index.ToString() + reg_abbr_continued, regname));
                        regs.Add(new KeyValuePair<string, string>(reg_abbr + reg_abbr_index2.ToString() + reg_abbr_continued, regname));
                        regs.Add(new KeyValuePair<string, string>(reg_abbr + reg_abbr_index3.ToString() + reg_abbr_continued, regname));
                        if(TableNameProcesser.GetName(m, 7) != "")
                        {
                            int reg_abbr_index4 = TableNameProcesser.GetNumber(m, 7);
                            regs.Add(new KeyValuePair<string, string>(reg_abbr + reg_abbr_index4.ToString() + reg_abbr_continued, regname));
                        }
                        if(TableNameProcesser.GetName(m,10) != "")
                        {
                            string reg_abbr2 = TableNameProcesser.GetName(m, 11);
                            int reg_abbr_index4 = TableNameProcesser.GetNumber(m, 12);
                            int reg_abbr_index5 = TableNameProcesser.GetNumber(m, 13);
                            int reg_abbr_index6 = TableNameProcesser.GetNumber(m, 14);

                            regs.Add(new KeyValuePair<string, string>(reg_abbr2 + reg_abbr_index4.ToString(), regname));
                            regs.Add(new KeyValuePair<string, string>(reg_abbr2 + reg_abbr_index5.ToString(), regname));
                            regs.Add(new KeyValuePair<string, string>(reg_abbr2 + reg_abbr_index6.ToString(), regname));
                        }
                    }},
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + @"([^\n\r\(\)=]+) \(" + register_abbr + dashcoloncomma + @"(.*?)\)" + bit_desc ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 2);

                        if(reg_abbr != "")
                            regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + register_long_name + @" ([0-9])( register)?" ,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        int reg_index = TableNameProcesser.GetNumber(m, 2);
                        string reg_abbr =  null;

                        if (regname == "Interrupt Set-Enable")
                            reg_abbr = "ISER" + reg_index;
                        else if (regname == "Interrupt Clear-Enable")
                            reg_abbr = "ICER" + reg_index;
                        else if (regname == "Interrupt Set-Pending")
                            reg_abbr = "ISPR" + reg_index;
                        else if (regname == "Interrupt Clear-Pending")
                            reg_abbr = "ICPR" + reg_index;
                        else if (regname == "Interrupt Active Bit")
                            reg_abbr = "IABR" + reg_index;
                        else if (regname == "Interrupt Priority")
                            reg_abbr = "IPR" + reg_index;

                        if(reg_abbr != null)
                            regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){// Total exception from LPC40XX
                    RegexPattern = table_beginning + "(Software Trigger Interrupt) Register",
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = "STIR";

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    new TableNameProcesser(){// Total exception from LPC43XX
                    RegexPattern = table_beginning + register_long_name + space + register_abbr + dashcoloncomma + "addresses" + space + register_addr + @"\)" + bit_desc,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 2);

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }},
                    //new TableNameProcesser(){// Blanket!
                    //RegexPattern = table_beginning + register_long_name + @"([0-9])? \(([A-Z0-9_\/\:\[\]]+)[,]? (.*?)\)",
                    //ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    //{
                    //    regname = m.Groups[1].ToString().Trim();
                    //    string reg_abbr = TableNameProcesser.GetName(m, 3);

                    //    regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    //}},
                    new TableNameProcesser(){// Blanket!
                    RegexPattern = table_beginning + register_long_name + @"[s]?([^\n\r\(\)]+)?" + space + @"\(([A-Za-z0-9_]+)([^\.\:]*?)\)" + bit_desc,
                    ProcessTableHeaderMatch = delegate (Match m, ref string regname, ref List<KeyValuePair<string, string>> regs)
                    {
                        regname = m.Groups[1].ToString().Trim();
                        string reg_abbr = TableNameProcesser.GetName(m, 3);

                        regs.Add(new KeyValuePair<string, string>(reg_abbr, regname));
                    }}
#endregion
            };

            TableNameProcesser min_processer = null;
            Match min_match = null;
            foreach (var processer in processors)
            {
                Regex regex = new Regex(processer.RegexPattern, RegexOptions.Singleline);
                Match m = regex.Match(file, start_index, end_index - start_index);
                if (m.Success && ((min_match == null) || (m.Index < min_match.Index)))
                {
                    min_match = m;
                    min_processer = processer;
                }
            }

            Regex test_regex = new Regex(table_beginning + register_long_name + @" [0-9]? (register)?", RegexOptions.Singleline);
            var mat = test_regex.Match(file, start_index, Math.Max(end_index - start_index, 0));

            if(min_processer == null)
                return false;

            min_processer.ProcessTableHeaderMatch(min_match, ref reg_name, ref registers);
            reg_name = min_match.Groups[0].ToString().Replace("\n", "").Replace("\r", "");

            // Table should not be contained twice in the match, that indicates bad match!
            if (min_match.Groups[0].ToString().TrimStart().Substring("Table".Length).Contains("Table")
                && !min_match.Groups[0].ToString().TrimStart().Substring("Table".Length).Contains("Hash Filter Table")
                 && !min_match.Groups[0].ToString().TrimStart().Substring("Table".Length).Contains("End of AF Tables"))
                Console.WriteLine("Too big subregister match " + reg_name + registers[0].Key);

            //Check the parsed register abbreviation
            if (registers.Count > 0)
            {
                foreach (var reg in registers)
                {
                    if ((reg.Key == null) || (reg.Key == ""))
                        throw new Exception("Failed to find register abbreviation!");

                    if (reg.Key.Contains('[') || reg.Key.Contains(']'))
                        throw new Exception("Failed to parse register abbreviation indexes!");

                    if (reg.Key.Contains(':') || reg.Key.Contains('/'))
                        throw new Exception("Failed to properly parse the register abbreviation!");
                }
            }
            start_index = min_match.Index + min_match.Length;

            //Update the end index
            Regex next_table_regex = new Regex(table_beginning);
            Match next_table_m = next_table_regex.Match(file, start_index, end_index - start_index);
            if (next_table_m.Success)
                end_index = next_table_m.Index;
            //min_processer = null;
            //min_match = null;
            //foreach (var processer in processors)
            //{
            //    Regex regex = new Regex(processer.RegexPattern, RegexOptions.Singleline);
            //    Match m = regex.Match(file, start_index, Math.Max(end_index - start_index, 0));
            //    if (m.Success && ((min_match == null) || (m.Index < min_match.Index)))
            //    {
            //        min_match = m;
            //        min_processer = processer;
            //    }
            //}

            //if(min_match != null)
            //    end_index = min_match.Index;

            return true;
        }

        private static Dictionary<string, int> ProcessTableHeaderRow(string file, ref int start_index, int end_index)
        {
            Dictionary<string, int> header_col_starts = new Dictionary<string, int>();

            if (file.Substring(start_index).TrimStart().StartsWith("PINSEL") || file.Substring(start_index).TrimStart().StartsWith("Reset value: "))//Special bit description table too complex to be handled
                return header_col_starts;

            Regex cols_regex = new Regex(@"([ ]+)(Bit[s]?|[A-Z0-9]+)?[ ]*(Symbo[l]?|Function|Name)?[ ]*(Valu[e]?)?[ ]*(Description|Function)[ ]*(Reset)?[ ]*(Access)?[ ]*(Reset)?([_ODe \n\r0-9]*([Vv]alue))?", RegexOptions.Singleline);
            Match cols_m = cols_regex.Match(file, start_index, end_index - start_index);

            if (!cols_m.Success)
                throw new Exception("No header row matched!");

            start_index = cols_m.Index + cols_m.Length;

            for (int i = 2; i < cols_m.Groups.Count - 1; i++)
            {
                string col_name = cols_m.Groups[i].ToString();

                // Fix typos and inconsistencies
                if (col_name == "Symbo")
                    col_name += "l";
                else if (col_name == "Bits")
                    col_name = "Bit";

                if ((col_name != "") && (i < (cols_m.Groups.Count - 2)))
                    header_col_starts.Add(col_name, cols_m.Groups[i].Index - cols_m.Groups[1].Index);
            }

            if (header_col_starts.Count < 2)
                throw new Exception("Too few headers found!");

            return header_col_starts;
        }

        private static List<HardwareSubRegister> ProcessTableRows(string file, ref int start_index, int end_index, Dictionary<string, int> headerColumns)
        {
            Regex contains_table_regex = new Regex(@"[\r]?\n[ ]*Table [0-9]+[\.:][ ]{2,}");
            if (contains_table_regex.Match(file, start_index, end_index - start_index).Success)
                throw new Exception("Table rows contain another table");

            List<HardwareSubRegister> subregs = new List<HardwareSubRegister>();

            if ((headerColumns == null) || (headerColumns.Count == 0))
                return null;

            bool none_found = true; // Used for checking only

            int bit_index = -1;
            if(headerColumns.ContainsKey("Bit"))
                bit_index = headerColumns["Bit"];
            int symbol_index;
            if (headerColumns.ContainsKey("Symbol"))
                symbol_index = headerColumns["Symbol"];
            else if (headerColumns.ContainsKey("Name"))
                symbol_index = headerColumns["Name"];
            else if (headerColumns.ContainsKey("Function"))
                symbol_index = headerColumns["Function"];
            else
            {
                if (!file.Substring(start_index).TrimStart().StartsWith("31:0"))
                    throw new Exception("Failed to parse any subregister table rows!");
                return subregs;// No symbol/function means there are no subregisters
            }

            string bit_spacing_sub_regex = @"[ ]{" + ((bit_index == -1) ? 0 : (bit_index - 1)) + "," + ((bit_index == -1) ? symbol_index : (bit_index + 1)) + @"}";
            // symbol spacing logic assumes that 5 is the maximum length of the bit value
            Regex row_regex = new Regex(@"\r\n" + bit_spacing_sub_regex + @"([0-9]{1,2})([\:][ ]?([0-9]{1,2})?)?[\[]?[0-9]?[\]]?[\[]?[0-9]?[\]]?[ ]{" + Math.Max(1, (bit_index != -1)? (symbol_index - bit_index - 5) : 1) + "," + (symbol_index - bit_index) + @"}([a-zA-Z0-9_\-\.]+)", RegexOptions.Singleline);
            // symbol spacing logic assumes that 3 is the maximum length of the bit value on the first row
            Regex split_row_regex = new Regex(@"\r\n" + bit_spacing_sub_regex + @"([0-9]{1,2})\:[ ]{" + Math.Max(1, (bit_index != -1)? (symbol_index - bit_index - 3) : 1) + "," + (symbol_index - bit_index) + @"}([a-zA-Z0-9_\-\.]+).*?\r\n" + bit_spacing_sub_regex + @"([0-9]+)[ ]*[\r]?[\n]?", RegexOptions.Singleline);
            Match row_m = row_regex.Match(file, start_index, end_index - start_index);
            Match split_row_m = split_row_regex.Match(file, start_index, end_index - start_index);

            while (split_row_m.Success || row_m.Success)
            {
                string subreg_name = TableNameProcesser.GetName(row_m, 4);
                int bit = TableNameProcesser.GetNumber(row_m, 1);
                bool is_bit_range = (TableNameProcesser.GetName(row_m, 2).Length != 0);
                int bit_start = bit;
                if (is_bit_range && (TableNameProcesser.GetName(row_m, 3) != ""))
                    bit_start = TableNameProcesser.GetNumber(row_m, 3);

                 if (split_row_m.Success && ((split_row_m.Index < row_m.Index) || ((split_row_m.Index == row_m.Index) && (split_row_m.Length > row_m.Length))))
                {
                    row_m = split_row_m;
                    subreg_name = TableNameProcesser.GetName(row_m, 2);
                    bit_start = TableNameProcesser.GetNumber(row_m, 3);
                }

                if ((bit_start > 31) || (bit > 31))
                    throw new Exception("Strange bit range found! Expecting 32-bit registers only!");

                if ((subreg_name != "-") && (subreg_name != "Unimplemented"))
                    subregs.Add(new HardwareSubRegister
                    {
                        Name = subreg_name,
                        FirstBit = is_bit_range ? bit_start : bit,
                        SizeInBits = is_bit_range ? (bit - bit_start + 1) : 1
                    });

                start_index = row_m.Index + row_m.Length - ((row_m.Groups[0].ToString().EndsWith("\r\n"))? 2 : 0);
                none_found = false;

                row_m = row_regex.Match(file, start_index, end_index - start_index);
                split_row_m = split_row_regex.Match(file, start_index, end_index - start_index);
            }

            if (none_found)
                throw new Exception("Failed to parse any subregister table rows!");

            if (subregs.Count == 0)
                return null;
            return subregs;
        }

        // Function modified from Kinetis
        private static HardwareRegister[] ProcessStructContents(string headerName, 
            Family family,
            ref Dictionary<string, HardwareRegisterSet> other_types, Dictionary<string, ulong> dict_type_sizes,
            string structName, string structContents, bool insideUnion,
            out ulong structSize,
            ref Dictionary<string, string> nested_types)
        {
            if (structContents.Contains("#endif"))
                structContents = PreprocessIfDefs(structContents, family.Defines);

            List<HardwareRegister> regs = new List<HardwareRegister>();

            ulong hex_offset = 0;
            structSize = 0;

            Regex reg_regex = new Regex(@"^[ \t]*(const|__I|__O|__IO)?[ \t]*([a-zA-Z0-9_]+)[ ]+([^ \n\r\[\]]+)(\[([a-zA-Z0-9_ \+]*)\])?(\[([a-zA-Z0-9_ \+]*)\])?;", RegexOptions.Multiline);
            Regex union_regex = new Regex(@"union {(.+?)}[ ]*([^ \n\r\[\]]*)(\[([a-zA-Z0-9_ \+]*)\])?(\[([a-zA-Z0-9_ \+]*)\])?;$", RegexOptions.Singleline);
            Regex union_beginning_regex = new Regex(@"union ({)", RegexOptions.Singleline);
            Regex inner_struct_regex = new Regex(@"struct[ \n\r]*{(.+?)}[ ]*([^ \n\r\[\]]*)(\[([a-zA-Z0-9_ \+]*)\])?(\[([a-zA-Z0-9_ \+]*)\])?;", RegexOptions.Singleline);

            Match union_m = null, inner_struct_m = null, reg_m = null;
            int struct_regs_index = 0;
            while (true)
            {
                union_m = union_beginning_regex.Match(structContents, struct_regs_index);
                inner_struct_m = inner_struct_regex.Match(structContents, struct_regs_index);
                reg_m = reg_regex.Match(structContents, struct_regs_index);

                if (!(union_m.Success || inner_struct_m.Success || reg_m.Success))
                    break;

                if (reg_m.Success && !(union_m.Success && (union_m.Index < reg_m.Index)) && !(inner_struct_m.Success && (inner_struct_m.Index < reg_m.Index)))// next match is a register
                {
                    string reg_io_type = reg_m.Groups[1].ToString();
                    string reg_type = reg_m.Groups[2].ToString();
                    string reg_name = reg_m.Groups[3].ToString();
                    int reg_array_size = ParseNumericValue(headerName, reg_m.Groups[5].Value, family.Defines);
                    int reg_array_size2 = ParseNumericValue(headerName, reg_m.Groups[7].Value, family.Defines);

                    bool reg_readonly = false;
                    switch (reg_io_type)
                    {
                        case "__I":
                        case "const":
                            reg_readonly = true;
                            break;
                        case "__O":
                        case "__IO":
                        case "":
                            reg_readonly = false;
                            break;
                        default:
                            throw new Exception("Unknown IO type parsed!");
                    }

                    struct_regs_index = reg_m.Index + reg_m.Length;

                    if (reg_name.StartsWith("RESERVED", StringComparison.InvariantCultureIgnoreCase) || reg_name.StartsWith("unused", StringComparison.InvariantCultureIgnoreCase))
                    {
                        hex_offset += (ulong)reg_array_size * (ulong)reg_array_size2 * (ulong)(dict_type_sizes[reg_type] / 8.0);
                        if (insideUnion)
                            throw new Exception("There should be no RESERVED registers inside unions!");
                        continue; // RESERVED registers are not true registers, do not save them
                    }

                    for (int i = 1; i <= reg_array_size; i++)
                    {
                        for (int j = 1; j <= reg_array_size2; j++)
                        {
                            string name = reg_name;
                            if (reg_array_size != 1)
                                name += "_" + (i - 1).ToString();
                            if (reg_array_size2 != 1)
                                name += "_" + (j - 1).ToString();

                            try
                            {
                                if (!STANDARD_TYPE_SIZES.Keys.Contains(reg_type))
                                    nested_types[structName + "+" + name] = reg_type;

                                regs.Add(new HardwareRegister()
                                {
                                    Name = name,
                                    SizeInBits = (int)dict_type_sizes[reg_type],
                                    Address = FormatToHex(hex_offset),
                                    ReadOnly = reg_readonly
                                });
                                hex_offset += (ulong)(dict_type_sizes[reg_type] / 8.0);
                            }
                            catch (KeyNotFoundException ex)
                            {
                                Console.WriteLine("Type undefined:   " + reg_type + " in " + headerName);
                            }
                            if (insideUnion && (structSize == 0))
                                structSize = (ulong)reg_array_size * (ulong)reg_array_size2 * (ulong)(dict_type_sizes[reg_type] / 8.0);
                        }
                    }
                    if (insideUnion)
                        hex_offset = 0;
                }
                else if (union_m.Success && !(reg_m.Success && (reg_m.Index < union_m.Index)) && !(inner_struct_m.Success && (inner_struct_m.Index < union_m.Index)))// next match is an union
                {
                    // Find the entire union, knowing that the beginning is already found
                    int union_end_index = FindClosingBracket(structContents, union_m.Groups[1].Index);
                    union_end_index = structContents.IndexOf(';', union_end_index);
                    union_m = union_regex.Match(structContents, union_m.Index, union_end_index - union_m.Index + 1);
                    if (!union_m.Success)
                        throw new Exception("Failed to parse known union!");
                    string union_contents = union_m.Groups[1].ToString();
                    string union_name = union_m.Groups[2].ToString();
                    int union_array_size = ParseNumericValue(headerName, union_m.Groups[4].Value, family.Defines);
                    int union_array_size2 = ParseNumericValue(headerName, union_m.Groups[6].Value, family.Defines);

                    ulong size;
                    Dictionary<string, string> union_nested_types = new Dictionary<string, string>();
                    HardwareRegister[] union_regs = ProcessStructContents(headerName, family, ref other_types, dict_type_sizes, structName, union_contents, true, out size, ref union_nested_types);

                    for (int i = 1; i <= union_array_size; i++)
                    {
                        for (int j = 1; j <= union_array_size2; j++)
                        {
                            string prefix = "";
                            if(union_name != "")
                                prefix = union_name + "_";

                            string suffix = "";
                            if (union_array_size != 1)
                                suffix += "_" + i.ToString();
                            if (union_array_size2 != 1)
                                suffix += "_" + j.ToString();

                            foreach (var union_reg in union_regs)
                            {
                                HardwareRegister cpy = DeepCopy(union_reg);

                                if (union_nested_types.ContainsKey(structName + "+" + cpy.Name) && !STANDARD_TYPE_SIZES.Keys.Contains(union_nested_types[structName + "+" + cpy.Name]))
                                    nested_types[structName + "+" + prefix + cpy.Name + suffix] = union_nested_types[structName + "+" + cpy.Name];

                                cpy.Name = prefix + cpy.Name + suffix;
                                cpy.Address = FormatToHex(ParseHex(cpy.Address) + hex_offset);

                                regs.Add(cpy);
                            }
                            if (!insideUnion)
                                hex_offset += size;
                        }
                    }

                    struct_regs_index = union_m.Index + union_m.Length;
                }
                else if (inner_struct_m.Success && !(union_m.Success && (union_m.Index < inner_struct_m.Index)) && !(reg_m.Success && (reg_m.Index < inner_struct_m.Index)))// next match is a struct
                {
                    string inner_struct_name = inner_struct_m.Groups[2].ToString();
                    string inner_struct_contents = inner_struct_m.Groups[1].ToString();
                    int inner_struct_array_size = ParseNumericValue(headerName, inner_struct_m.Groups[4].Value, family.Defines);
                    int inner_struct_array_size2 = ParseNumericValue(headerName, inner_struct_m.Groups[6].Value, family.Defines);

                    ulong inner_struct_size;
                    Dictionary<string, string> inner_nested_types = new Dictionary<string, string>();
                    HardwareRegister[] struct_regs = ProcessStructContents(headerName, family, ref other_types, dict_type_sizes, structName, inner_struct_contents, false, out inner_struct_size, ref inner_nested_types);

                    for (int i = 1; i <= inner_struct_array_size; i++)
                    {
                        for (int j = 1; j <= inner_struct_array_size2; j++)
                        {
                            string prefix = "";
                            if (inner_struct_name != "")
                                prefix = inner_struct_name + "_";

                            string suffix = "";
                            if (inner_struct_array_size != 1)
                                suffix += "_" + i.ToString();
                            if (inner_struct_array_size2 != 1)
                                suffix += "_" + j.ToString();

                            foreach (var struct_reg in struct_regs)
                            {
                                HardwareRegister cpy = DeepCopy(struct_reg);

                                if (inner_nested_types.ContainsKey(structName + "+" + cpy.Name) && !STANDARD_TYPE_SIZES.Keys.Contains(inner_nested_types[structName + "+" + cpy.Name]))
                                    nested_types[structName + "+" + prefix + cpy.Name + suffix] = inner_nested_types[structName + "+" + cpy.Name];

                                cpy.Name = prefix + cpy.Name + suffix;
                                cpy.Address = FormatToHex(ParseHex(cpy.Address) + hex_offset);

                                regs.Add(cpy);
                            }
                            if (!insideUnion)
                                hex_offset += inner_struct_size;
                            else
                            {
                                structSize = inner_struct_size;
                                if ((inner_struct_array_size != 1) || (inner_struct_array_size2 != 1))
                                    throw new Exception("Structures inside unions are not expected to be arrays!");
                            }
                        }
                    }

                    struct_regs_index = inner_struct_m.Index + inner_struct_m.Length;
                }
                else
                {
                    throw new Exception("Cannot parse struct contents!");
                }
            }

            if (!insideUnion)
                structSize = hex_offset;

            return regs.ToArray();
        }

        // This is the most difficult and only define statement inside a struct that is not automatically parsed in ssp_13xx.h
        private static List<string> SPECIAL_ALWAYS_TRUE_BLOCK_BEGIN = new List<string>() { "#if !defined(CHIP_LPC110X) && !defined(CHIP_LPC11XXLV) && !defined(CHIP_LPC11AXX) && \\"};
        private static List<string> SPECIAL_SKIP_LINE = new List<string>() { "!defined(CHIP_LPC11CXX) && !defined(CHIP_LPC11EXX) && !defined(CHIP_LPC11UXX)" };

        // This function cannot handle complex ifdef statements or nested ifdefs, will throw if sth like that is detected
        private static string PreprocessIfDefs(string code, Dictionary<string, int> knownDefines)
        {
            List<string> code_lines = new List<string>();

            bool inside_block = false, add_block = false;
            bool one_block_added = false;
            foreach (var line in code.Split(new string[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries))
            {
                Regex if_regex = new Regex(@"^#if (defined\()?([^ \(\)]+)(\))?( \|\| defined\()?([^ \(\)]*)(\))?$");
                Regex ifdef_regex = new Regex(@"^#ifdef ([^ \(\)]+)$");
                Regex elif_regex = new Regex(@"^#elif defined\((.+)\)$");
                const string ELSE = "#else";
                const string ENDIF = "#endif";

                if (SPECIAL_SKIP_LINE.Contains(line.Trim()))
                    continue;

                Match m;
                if(((m = if_regex.Match(line)).Success) || SPECIAL_ALWAYS_TRUE_BLOCK_BEGIN.Contains(line.Trim()))
                {
                    string def = m.Groups[2].ToString();
                    string def2 = m.Groups[5].ToString();

                    if (inside_block)
                        throw new Exception("Nesting not supported!");

                    inside_block = true;
                    if ((knownDefines.ContainsKey(def) && (knownDefines[def] == 1)) || (knownDefines.ContainsKey(def2) && (knownDefines[def2] == 1)))
                        add_block = one_block_added = true;
                    else
                        add_block = one_block_added = false;
                    continue;
                }
                else if((m = ifdef_regex.Match(line)).Success)
                {
                    string def = m.Groups[1].ToString();

                    if (inside_block)
                        throw new Exception("Nesting not supported!");

                    inside_block = true;
                    if (knownDefines.ContainsKey(def) && (knownDefines[def] == 1))
                        add_block = one_block_added = true;
                    else
                        add_block = one_block_added = false;
                    continue;
                }
                else if ((m = elif_regex.Match(line)).Success)
                {
                    string def = m.Groups[1].ToString();

                    if (!inside_block)
                        throw new Exception("#elif found before #if(def)!");

                    inside_block = true;
                    if (knownDefines.ContainsKey(def) && (knownDefines[def] == 1))
                    {
                        if (one_block_added)
                            throw new Exception("Another block was added before this elif statement!");
                        add_block = one_block_added = true;
                    }
                    else
                        add_block = false;
                    continue;
                }
                else if (line.Trim() == ELSE)
                {
                    if (!inside_block)
                        throw new Exception("#else found before #if(def)!");

                    inside_block = true;
                    if(!one_block_added)
                        add_block = true;
                    continue;
                }
                else if (line.TrimStart().StartsWith(ENDIF))
                {
                    if (!inside_block)
                        throw new Exception("#endif found before #if(def)!");
                    inside_block = add_block = one_block_added = false;
                    continue;
                }

                if (line.Contains("#ifdef") || line.Contains("#if") || line.Contains("#elif") || line.Contains("#else") || line.Contains("#endif"))
                    throw new Exception("Regexes failed to catch a define!");

                if(!inside_block || (inside_block && add_block))
                    code_lines.Add(line);
            }

            return string.Join("\r\n", code_lines);
        }

        private static int FindClosingBracket(string str, int openingBracketIndex)
        {
            int index = openingBracketIndex;

            char opening_bracket, closing_bracket;
            switch(str[openingBracketIndex])
            {
                case '{':
                    opening_bracket = '{';
                    closing_bracket = '}';
                    break;
                default:
                    throw new Exception("No known opening bracket found in string!");
            }

            int num_opening_brackets = 1;
            int num_closing_brackets = 0;
            while(num_closing_brackets != num_opening_brackets)
            {
                index = str.IndexOfAny(new char[] { opening_bracket, closing_bracket }, index + 1);
                if (str[index] == opening_bracket)
                    num_opening_brackets++;
                else if (str[index] == closing_bracket)
                    num_closing_brackets++;
            }

            return index;
        }

        private static int ParseNumericValue(string headerName, string value, Dictionary<string, int> knownDefines)
        {
            int parsed_value = 1;
            try
            {
                parsed_value = string.IsNullOrEmpty(value) ? 1 : Int32.Parse(value);
            }
            catch (FormatException ex)
            {
                string parsed_value_string = value;

                try
                {
                    foreach (var key in knownDefines.Keys)
                    {
                        parsed_value_string = parsed_value_string.Replace(key, knownDefines[key].ToString());
                    }

                    if (parsed_value_string.EndsWith(" + 1"))
                        parsed_value = Int32.Parse(parsed_value_string.Substring(0, parsed_value_string.Length - " + 1".Length)) + 1;
                    else
                        parsed_value = string.IsNullOrEmpty(parsed_value_string) ? 1 : Int32.Parse(parsed_value_string);
                }
                catch (FormatException ex2)
                {
                    if (parsed_value_string != "")
                        Console.WriteLine("Numeric value: " + parsed_value_string + " in " + headerName);
                }
            }

            return parsed_value;
        }

        private static ulong ParseHex(string hex)
        {
            if (hex.StartsWith("0x"))
                hex = hex.Substring(2);
            return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
        }

        private static string FormatToHex(ulong addr, int length = 32)
        {
            string format = "0x{0:x" + length / 4 + "}";
            return string.Format(format, (uint)addr);
        }

        private static HardwareRegisterSet DeepCopy(HardwareRegisterSet set)
        {
            HardwareRegisterSet set_new = new HardwareRegisterSet
            {
                UserFriendlyName = set.UserFriendlyName,
                ExpressionPrefix = set.ExpressionPrefix,
            };

            if (set.Registers != null)
            {
                set_new.Registers = new HardwareRegister[set.Registers.Length];
                for (int i = 0; i < set.Registers.Length; i++)
                {
                    set_new.Registers[i] = DeepCopy(set.Registers[i]);
                }
            }

            return set_new;
        }

        private static HardwareRegister DeepCopy(HardwareRegister reg)
        {
            HardwareRegister reg_new = new HardwareRegister
            {
                Name = reg.Name,
                Address = reg.Address,
                GDBExpression = reg.GDBExpression,
                ReadOnly = reg.ReadOnly,
                SizeInBits = reg.SizeInBits
            };

            if (reg.SubRegisters != null)
            {
                reg_new.SubRegisters = new HardwareSubRegister[reg.SubRegisters.Length];
                for (int i = 0; i < reg.SubRegisters.Length; i++)
                {
                    reg_new.SubRegisters[i] = DeepCopy(reg.SubRegisters[i]);
                }
            }

            return reg_new;
        }

        private static HardwareSubRegister DeepCopy(HardwareSubRegister subreg)
        {
            HardwareSubRegister subreg_new = new HardwareSubRegister
            {
                Name = subreg.Name,
                FirstBit = subreg.FirstBit,
                SizeInBits = subreg.SizeInBits,
                KnownValues = (subreg.KnownValues != null) ? (KnownSubRegisterValue[])subreg.KnownValues.Clone() : null
            };

            return subreg_new;
        }
    }
}
