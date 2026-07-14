using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DriverTest
{
    public static class AGLinkCRC32
    {
        #region CRC32 Lookup Table

        /// <summary>
        /// Standard CRC32 lookup table (256 entries).
        /// This matches the table at DLL file offset 0x341000 (RVA 0x342000 in .rdata).
        /// </summary>
        private static readonly uint[] Table = GenerateTable();

        private static uint[] GenerateTable()
        {
            var table = new uint[256];
            const uint polynomial = 0xEDB88320;

            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }

            return table;
        }

        #endregion

        #region Core CRC32 Implementation

        /// <summary>
        /// Computes the CRC32 checksum exactly as AGLink40_x64.dll does.
        ///
        /// Matches the DLL function sub_180313070:
        ///   1. NOT the initial value: crc = init ^ 0xFFFFFFFF
        ///   2. For each byte: crc = (crc >> 8) ^ table[(crc ^ byte) & 0xFF]
        ///   3. NOT the result: return crc ^ 0xFFFFFFFF
        ///
        /// When called with init=0 (default), this returns standard CRC32.
        /// </summary>
        /// <param name="data">Input data bytes</param>
        /// <param name="init">Initial CRC value. Pass 0 for standard CRC32 (matching DLL behavior).</param>
        /// <returns>32-bit CRC checksum</returns>
        public static uint Compute(byte[] data, uint init = 0)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            // Step 1: NOT the initial value (DLL: "not ecx")
            uint crc = init ^ 0xFFFFFFFF;

            // Step 2: Table-driven CRC32 loop
            // (DLL inner loop at VA 0x1803130BC-0x1803130D6)
            for (int i = 0; i < data.Length; i++)
            {
                uint index = (crc ^ data[i]) & 0xFF;
                crc = (crc >> 8) ^ Table[index];
            }

            // Step 3: NOT the result (DLL: "not ecx; mov eax, ecx")
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>
        /// Strips array index brackets from the LAST segment of a symbol path,
        /// matching DLL behavior. Only the final path component's [...] is removed.
        ///
        ///   "Program.MyArray[0]"       → "Program.MyArray"
        ///   "Program.Arr[1].Field1"   → "Program.Arr[1].Field1"   (unchanged: last segment "Field1" has no [])
        ///   "Program.Values[3]"       → "Program.Values"
        ///   "DB1.Speed[0,1]"          → "DB1.Speed"
        /// </summary>
        /// <param name="path">Full symbol path</param>
        /// <returns>Path with array index stripped from the last segment</returns>
        public static string StripArrayIndices(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Find the last '.' — the last segment is after it
            int lastDot = path.LastIndexOf('.');
            string prefix = lastDot >= 0 ? path.Substring(0, lastDot + 1) : "";
            string lastSegment = lastDot >= 0 ? path.Substring(lastDot + 1) : path;

            // Strip [...] from the last segment only
            int bracket = lastSegment.IndexOf('[');
            if (bracket >= 0)
                lastSegment = lastSegment.Substring(0, bracket);

            return prefix + lastSegment;
        }

        /// <summary>
        /// Computes CRC32 for a symbol path + internal ID, matching DLL behavior.
        ///
        /// This is THE method to use for S7CommPlus symbol CRC computation.
        /// It takes the full path and internal ID (from the TIA Portal symbol table),
        /// strips array indices from the last segment, and computes CRC32 over:
        ///   [internalId as 4-byte LE] + [stripped path as UTF-8]
        ///
        /// Examples:
        ///   ComputeForSymbol("Program.MyArray[0]", internalId: 0x0642)
        ///     → CRC32( 0x42060000 ++ "Program.MyArray" )
        ///   ComputeForSymbol("Program.Struct1.Var1", internalId: 0)
        ///     → CRC32( 0x00000000 ++ "Program.Struct1.Var1" )
        /// </summary>
        /// <param name="symbolPath">
        /// Full symbol path as passed to CreateAccessByPath.
        /// Array indices in last segment are automatically stripped.
        /// </param>
        /// <param name="internalId">
        /// Symbol's internal ID from the TIA Portal project symbol table.
        /// This is the numeric ID assigned by the PLC during Explore (0x04BB).
        /// Pass 0 if unknown or if the symbol has no internal ID.
        /// </param>
        /// <param name="encoding">Text encoding (default: UTF-8)</param>
        /// <returns>32-bit CRC value for S7CommPlus subscription</returns>
        public static uint ComputeForSymbol(string symbolPath, uint internalId = 0, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(symbolPath))
                throw new ArgumentNullException(nameof(symbolPath));

            string stripped = StripArrayIndices(symbolPath);
            encoding = encoding ?? Encoding.UTF8;

            // Build input: [internalId LE 4 bytes] + [path UTF-8 bytes]
            byte[] idBytes = new byte[4];
            idBytes[0] = (byte)(internalId & 0xFF);
            idBytes[1] = (byte)((internalId >> 8) & 0xFF);
            idBytes[2] = (byte)((internalId >> 16) & 0xFF);
            idBytes[3] = (byte)((internalId >> 24) & 0xFF);

            byte[] pathBytes = encoding.GetBytes(stripped);
            byte[] data = new byte[4 + pathBytes.Length];
            Buffer.BlockCopy(idBytes, 0, data, 0, 4);
            Buffer.BlockCopy(pathBytes, 0, data, 4, pathBytes.Length);

            return Compute(data, init: 0);
        }

        /// <summary>
        /// Incremental CRC32 update — allows feeding data in chunks.
        /// Pass the previous CRC result as <paramref name="init"/> for the next chunk.
        ///
        /// This matches calling the DLL function with a non-zero init value,
        /// e.g., for combining CRC("hello") and CRC("world"):
        ///   uint partial = AGLinkCRC32.Compute(Encoding.ASCII.GetBytes("hello"), 0);
        ///   uint combined = AGLinkCRC32.Compute(Encoding.ASCII.GetBytes("world"), partial);
        /// </summary>
        public static uint ComputeIncremental(byte[] data, uint previousCrc)
        {
            return Compute(data, init: previousCrc);
        }

        #endregion

        #region Protocol Constants

        /// <summary>
        /// The symbol_crc field in GetMultiVar (0x054C) read requests is
        /// ALWAYS 0x00000000. The PLC identifies symbols by area_sub
        /// (internal ID assigned during subscription), not by CRC.
        /// This is verified against all Wireshark capture packets.
        /// </summary>
        public const uint GetMultiVarSymbolCrc = 0x00000000;

        #endregion

        #region Convenience Methods

        /// <summary>
        /// Computes CRC32 for a symbol path and returns it as a hex string.
        /// </summary>
        public static string ComputeHex(string symbolPath, uint internalId = 0)
        {
            uint crc = ComputeForSymbol(symbolPath, internalId);
            string stripped = StripArrayIndices(symbolPath);
            string info = stripped != symbolPath ? $" (stripped: \"{stripped}\")" : "";
            string idInfo = internalId != 0 ? $" [internalId=0x{internalId:X}]" : "";
            return $"0x{crc:X8}{info}{idInfo}";
        }

        /// <summary>
        /// Builds a complete S7CommPlus subscription entry for the given symbol.
        /// Format: [CRC32(4 LE)] [StrippedNameLength(4 LE)] [StrippedName(N)]
        /// Internal ID is prepended to path bytes before CRC computation.
        /// </summary>
        public static byte[] BuildSubscriptionEntry(string symbolPath, uint internalId = 0)
        {
            string stripped = StripArrayIndices(symbolPath);
            uint crc = ComputeForSymbol(symbolPath, internalId);
            byte[] nameBytes = Encoding.UTF8.GetBytes(stripped);

            byte[] entry = new byte[4 + 4 + nameBytes.Length];

            entry[0] = (byte)(crc & 0xFF);
            entry[1] = (byte)((crc >> 8) & 0xFF);
            entry[2] = (byte)((crc >> 16) & 0xFF);
            entry[3] = (byte)((crc >> 24) & 0xFF);

            uint len = (uint)nameBytes.Length;
            entry[4] = (byte)(len & 0xFF);
            entry[5] = (byte)((len >> 8) & 0xFF);
            entry[6] = (byte)((len >> 16) & 0xFF);
            entry[7] = (byte)((len >> 24) & 0xFF);

            Array.Copy(nameBytes, 0, entry, 8, nameBytes.Length);
            return entry;
        }

        /// <summary>
        /// Gets the CRC32 table entry at the given index (0-255).
        /// Useful for verifying the table matches the DLL's table.
        /// </summary>
        public static uint GetTableEntry(int index)
        {
            if (index < 0 || index > 255)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Table[index];
        }

        /// <summary>
        /// Verifies the implementation against known test vectors.
        /// Returns true if all tests pass.
        /// </summary>
        public static bool SelfTest()
        {
            var testCases = new (string Input, uint Expected)[]
            {
                ("test",              0xD87F7E0C),
                ("123456789",         0xCBF43926),
                ("",                  0x00000000),
                ("fcVector",          0x64F8CCE3),
                ("fcTorqCal",         0xA3830BA9),
                ("fcDnLmtCal",        0x9B9850A3),
                ("DriveNXP:I",        0x1DC42C0F),
                ("ASRoot",            0x9DF74408),
                ("PLCProgram",        0x05164A45),
                ("SWEvents",          0xBD100FAA),
            };

            bool allPassed = true;
            foreach (var (input, expected) in testCases)
            {
                byte[] data = Encoding.ASCII.GetBytes(input);
                uint result = Compute(data);

                if (result != expected)
                {
                    Console.WriteLine($"FAIL: CRC32(\"{input}\") = 0x{result:X8} (expected: 0x{expected:X8})");
                    allPassed = false;
                }
            }

            if (allPassed)
                Console.WriteLine("All CRC32 self-tests passed.");

            return allPassed;
        }

        #endregion
    }
}
