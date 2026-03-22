# π Generator — WPF

A dark-themed WPF desktop app that computes and streams digits of π in real time,
matching the aesthetic of the browser version.

## Requirements

- Windows 10/11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Visual Studio 2022 (v17.8+) **or** just the `dotnet` CLI

## How to run

### Option A — Visual Studio
1. Open `PiGenerator.sln`
2. Press **F5** (or Ctrl+F5 to run without debugger)

### Option B — CLI
```
cd PiGenerator
dotnet run
```

To build a standalone EXE:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Output lands in `bin\Release\net9.0-windows\win-x64\publish\PiGenerator.exe`

## Algorithm

Uses the **Chudnovsky algorithm** with `System.Numerics.BigInteger` — no NuGet
packages required. Converges at ~14.18 decimal digits per term, making it far
faster than the streaming spigot used in the browser version.

Computation runs on a background `Task` so the UI stays responsive at all times.
Digits are revealed in progressive doubling chunks (1k → 2k → 4k → … → 200k),
so you see output immediately and throughput scales up over time.

## Controls

| Button   | Action                              |
|----------|-------------------------------------|
| ⏸ Pause  | Suspend computation and the clock   |
| ▶ Resume | Continue from where it stopped      |
| ⎘ Copy   | Copy "3.{all digits}" to clipboard  |
| ↺ Restart| Reset everything and start fresh    |
