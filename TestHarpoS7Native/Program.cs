using HarpoS7Wrapper;
using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace TestHarpoS7Native
{
    class Program
    {
        private static byte[] sessionKey = null;
        static void Main(string[] args)
        {
            ExcuteTest();
            Console.ReadKey();
        }
        static byte[] challenge = new byte[] { 0x62, 0xd7, 0x9d, 0x7d, 0x6d, 0x73, 0x6f, 0xa3, 0x08, 0xa9, 0x13, 0x19, 0xee, 0xb9, 0xd3, 0xfa, 0x1d, 0xfd, 0x66, 0x94 };
        static string PlcSimFingerprint = "03:09013727CCBFBF3C";
        static string RealPlcFingerprint = "00:181B7B0847D11694";

        private static void ExcuteTest()
        {
            // Test both PlcSim and RealPlc with key-reuse mode
            TestWithFingerprint(PlcSimFingerprint);
            Console.WriteLine();
            TestWithFingerprint(RealPlcFingerprint);
        }

        private static void TestWithFingerprint(string plcFingerprint)
        {
            Console.WriteLine($"=== {plcFingerprint} ===");
            var store = new DefaultPublicKeyStore();
            var publicKey = new byte[store.GetPublicKeyLength(plcFingerprint)];
            try
            {
                store.ReadPublicKey(publicKey, plcFingerprint);
            }
            catch (UnknownPublicKeyException)
            {
                Console.WriteLine("[-] Public key not found in key store.");
                return;
            }
            var family = plcFingerprint.ToPublicKeyFamily();

            int blobLen = (family != EPublicKeyFamily.PlcSim)
                ? Constants.EncryptedBlobLengthRealPlc
                : Constants.EncryptedBlobLengthPlcSim;
            var keyBlob = new byte[blobLen];
            sessionKey = new byte[Constants.SessionKeyLength];

            int iterations = 100;

            // === Test 1: No reuse (fresh keys every call) ===
            Console.Write("  Normal (fresh keys):      ");
            LegacyAuthenticationScheme.SetKeyReuseMode(0);
            // Warmup
            LegacyAuthenticationScheme.Authenticate(keyBlob, sessionKey, challenge, publicKey, family);

            var sw = Stopwatch.StartNew();
            long totalTicks = 0, minTicks = long.MaxValue, maxTicks = 0;
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                LegacyAuthenticationScheme.Authenticate(keyBlob, sessionKey, challenge, publicKey, family);
                sw.Stop();
                long ticks = sw.ElapsedTicks;
                totalTicks += ticks;
                if (ticks < minTicks) minTicks = ticks;
                if (ticks > maxTicks) maxTicks = ticks;
            }
            double avgMs = (totalTicks / (double)iterations) / (Stopwatch.Frequency / 1000.0);
            double minMs = minTicks / (Stopwatch.Frequency / 1000.0);
            double maxMs = maxTicks / (Stopwatch.Frequency / 1000.0);
            Console.WriteLine($"avg={avgMs:F3}ms min={minMs:F3}ms max={maxMs:F3}ms");

            // === Test 2: Reuse mode — 1st call (pays full price) ===
            Console.Write("  Reuse mode, 1st call:     ");
            LegacyAuthenticationScheme.SetKeyReuseMode(0); // clear saved state
            LegacyAuthenticationScheme.SetKeyReuseMode(1); // enable save-once-reuse
            sw.Restart();
            LegacyAuthenticationScheme.Authenticate(keyBlob, sessionKey, challenge, publicKey, family);
            sw.Stop();
            double firstMs = sw.ElapsedTicks / (Stopwatch.Frequency / 1000.0);
            Console.WriteLine($"{firstMs:F3}ms (generates + saves)");

            // Save the seed/IV from first call for verification
            byte[] savedKeyBlob = new byte[blobLen];
            Array.Copy(keyBlob, savedKeyBlob, blobLen);

            // === Test 3: Reuse mode — subsequent calls (reuses saved) ===
            Console.Write("  Reuse mode, subsequent:   ");
            totalTicks = 0; minTicks = long.MaxValue; maxTicks = 0;
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                LegacyAuthenticationScheme.Authenticate(keyBlob, sessionKey, challenge, publicKey, family);
                sw.Stop();
                long ticks = sw.ElapsedTicks;
                totalTicks += ticks;
                if (ticks < minTicks) minTicks = ticks;
                if (ticks > maxTicks) maxTicks = ticks;
            }
            avgMs = (totalTicks / (double)iterations) / (Stopwatch.Frequency / 1000.0);
            minMs = minTicks / (Stopwatch.Frequency / 1000.0);
            maxMs = maxTicks / (Stopwatch.Frequency / 1000.0);
            Console.WriteLine($"avg={avgMs:F3}ms min={minMs:F3}ms max={maxMs:F3}ms");

            // Verify seed/IV match between first and subsequent calls
            bool seedMatch = true;
            int firstDiff = -1;
            for (int i = 0; i < blobLen; i++)
            {
                if (keyBlob[i] != savedKeyBlob[i]) { seedMatch = false; firstDiff = i; break; }
            }
            if (seedMatch)
                Console.WriteLine($"  Seed/IV consistency check: PASS (identical, {blobLen} bytes)");
            else
            {
                Console.WriteLine($"  Seed/IV consistency check: FAIL at offset {firstDiff} " +
                    $"(first=0x{savedKeyBlob[firstDiff]:X2} reuse=0x{keyBlob[firstDiff]:X2})");
                // Show hex context around difference
                int start = Math.Max(0, firstDiff - 8);
                int end = Math.Min(blobLen, firstDiff + 24);
                Console.Write($"    Saved[{start}..{end}]: ");
                for (int i = start; i < end; i++) Console.Write($"{savedKeyBlob[i]:X2} ");
                Console.WriteLine();
                Console.Write($"    Reuse[{start}..{end}]: ");
                for (int i = start; i < end; i++) Console.Write($"{keyBlob[i]:X2} ");
                Console.WriteLine();
            }

            // Verify legitimation still works
            if (family == EPublicKeyFamily.PlcSim)
            {
                var blobData = new byte[LegitimateScheme.OutputBlobDataLengthPlcSim];
                LegitimateScheme.SolveLegitimateChallengePlcSim(blobData, challenge, publicKey, sessionKey, "12345678");
                Console.WriteLine($"  Legitimation: OK (blob={blobData.Length} bytes)");
            }
            else
            {
                var blobData = new byte[LegitimateScheme.OutputBlobDataLengthRealPlc];
                LegitimateScheme.SolveLegitimateChallengeRealPlc(blobData, challenge, publicKey, family, sessionKey, "12345678");
                Console.WriteLine($"  Legitimation: OK (blob={blobData.Length} bytes)");
            }

            // Cleanup
            LegacyAuthenticationScheme.SetKeyReuseMode(0);
        }
    }
}
