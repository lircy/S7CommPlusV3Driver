# S7CommPlusDriver

An open-source .NET implementation of the S7CommPlus protocol for communicating with Siemens S7-1200 / S7-1500 series PLCs.

---

## ⚠️ Important Notices

1. **This project is based on [thomas-v2/S7CommPlusDriver](https://github.com/thomas-v2/S7CommPlusDriver).** All credit for the original protocol implementation goes to Thomas Wiens.

2. **The non-TLS authentication algorithm (HarpoS7Native) is based on [bonk-dev/HarpoS7](https://github.com/bonk-dev/HarpoS7).** That repository provides the reverse-engineered implementation of the S7CommPlus non-TLS authentication and encryption primitives.

3. **This driver is still under active development and testing. DO NOT use it in production environments.** The protocol implementation may contain unknown defects, and production use could result in equipment downtime, data loss, or other severe consequences.

---

## Features

- Full S7CommPlus protocol stack (ProtocolVersion V1 / V2 / V3)
- TLS-encrypted connections with automatic detection and fallback
- PlcSim (soft PLC) and RealPlc (S7-1200/S7-1500 hardware) non-TLS authentication paths
- **True AnyCPU build**: single compilation output supporting both x86 and x64 runtimes
- Automatic key rotation (30-minute interval) driven by a high-precision multimedia timer
- Authentication key material reuse optimization (key rotation < 20 μs)
- Dynamic PLC variable tree browsing (nested UDT / FB / Struct / multi-dimensional arrays / Failsafe DBs)
- Batch read/write with automatic PDU-size-aware fragmentation
- Subscription and alarming support
- PLC operating state control (RUN / STOP)
- Tag comment retrieval (XML format, multi-language support)
- Password legitimation

## Project Structure

```
S7CommPlusDriver-master/
├── src/
│   ├── S7CommPlusDriver/        # Core protocol library
│   │   ├── Core/                # S7CommPlus protocol object model
│   │   ├── ClientApi/           # Client API (PlcTag, Browser, ItemAddress)
│   │   ├── Net/                 # Transport layer (S7Client, MsgSocket)
│   │   ├── OpenSSL/             # TLS P/Invoke layer (OpenSSL 3.x)
│   │   ├── Legitimation/        # Password authentications
│   │   ├── Alarming/            # Alarm handling
│   │   ├── Subscriptions/       # Subscription management
│   │   ├── MmTimer.cs           # High-precision multimedia timer (integrated)
│   │   └── S7CommPlusConnection.cs  # Main connection entry point
│   ├── HarpoS7Wrapper/          # Non-TLS auth/encryption P/Invoke wrappers
│   │   ├── HarpoS7Native.cs     # C++ DLL runtime dispatch (Impl64/Impl32)
│   │   └── Authentication.cs    # Auth API wrappers
│   ├── HarpoS7Native/           # C++ native authentication library
│   │   ├── src/auth/            # PlcSim / RealPlc authentication
│   │   ├── src/transforms/      # Monolith transforms (boolean circuits)
│   │   ├── src/monoliths/       # Auto-generated bit-operation code
│   │   └── include/harpos7/     # C API headers
│   ├── S7CommPlusGUIBrowser/    # GUI browser demo application
│   ├── DriverTest/              # Driver functional tests
│   └── TestHarpoS7Native/       # C++ native library benchmarks
├── MmTimer/                     # Original multimedia timer project (reference)
└── 更新履历.txt                 # Detailed changelog (Chinese)
```

## Build Requirements

- **.NET Framework 4.0+** (C# projects)
- **Visual Studio 2017+** or MSBuild 15.0+
- **C++ compiler** (MSVC 2017+), for building HarpoS7Native DLLs
- **OpenSSL 3.x** (libcrypto-3, libssl-3), for TLS connections
- **Windows** (multimedia timer depends on winmm.dll)

### Build Steps

1. Build the HarpoS7Native C++ DLLs (`HarpoS7Native_x86.dll` and `HarpoS7Native_x64.dll`)
2. Place the built DLLs into `src/HarpoS7Wrapper/`
3. Open `src/S7CommPlusDriver.sln`, select **Release | Any CPU**, build the solution
4. Output goes to `bin/` — includes `S7CommPlusDriver.dll` and all dependencies

## Quick Start

```csharp
var connection = new S7CommPlusConnection();

// Optional: set PLC info to influence ServerSession and SessionKey selection
connection.PLCInformation.MLFB = "6ES7 517-3FJ00-0AB0";
connection.PLCInformation.Firmware = "V2.2";

// Connect to PLC
int result = connection.Connect("192.168.0.1", password: "your_password");
if (result != 0)
    throw new Exception($"Connection failed: {result}");

// Read variables
var addressList = new List<ItemAddress>
{
    new ItemAddress { AccessArea = 0x8a0e0001, AccessSubArea = 0x00000002, LID = { 1 } }
};
connection.ReadValues(addressList, out var values, out var errors);

// Browse the variable tree
connection.Browse(out var varInfoList);

// Disconnect
connection.Disconnect();
```

## Technical Highlights

### Authentication Paths

| Path    | PLC Type                    | Crypto                                     | Notes                     |
|---------|-----------------------------|--------------------------------------------|---------------------------|
| TLS     | S7-1200/1500 (secure mode)  | OpenSSL 3.x                                | Auto-detected & switched  |
| PlcSim  | PLCSIM Advanced             | EC P-256 + AES-CTR + HMAC-SHA256           | Software simulation       |
| RealPlc | S7-1200/1500 (non-secure)   | Monolith boolean circuit transforms        | Physical hardware         |

### Key Rotation

After connection establishment, session keys are rotated automatically every 30 minutes. The first authentication generates fresh key material; subsequent rotations reuse the seed/IV (matching the behavior of Siemens' commercial AGLink driver), completing in under 20 μs for zero-impact key rotation.

### AnyCPU Runtime Dispatch

A single build output automatically selects the correct x64 or x86 native DLL (HarpoS7Native / OpenSSL) at runtime based on `Environment.Is64BitProcess`. No separate architecture-specific builds or distributions are needed.

## License

This project is licensed under the [GNU Lesser General Public License v3](https://www.gnu.org/licenses/lgpl-3.0.html), consistent with the original project.

```
S7CommPlusDriver
Copyright (C) 2023 Thomas Wiens, th.wiens@gmx.de

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU Lesser General Public License as
published by the Free Software Foundation, either version 3 of the
License, or (at your option) any later version.
```

## Acknowledgments

- [thomas-v2/S7CommPlusDriver](https://github.com/thomas-v2/S7CommPlusDriver) — Original S7CommPlus protocol .NET implementation
- [bonk-dev/HarpoS7](https://github.com/bonk-dev/HarpoS7) — Reverse-engineered S7CommPlus non-TLS authentication primitives
