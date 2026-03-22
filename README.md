# π Generator

A Windows desktop app that computes and displays digits of π in real time, built with WPF and .NET 9.

## Features

- Computes π indefinitely using the Chudnovsky algorithm
- Smooth animated digit display that self-tunes to your hardware
- Live stats: digit count, elapsed time, and digits/sec
- Pause, resume, restart, and copy digits to clipboard

## Requirements

- Windows 10/11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or SDK if building from source)

## Getting Started

### Run from source

```
git clone https://github.com/entorge/Pi-Generator
cd pi-generator
dotnet run --project PiGenerator
```

### Build a standalone executable

```
dotnet publish PiGenerator -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output will be at `PiGenerator/bin/Release/net9.0-windows/win-x64/publish/PiGenerator.exe`.

## How It Works

### Algorithm: Chudnovsky with Binary Splitting

π is computed using the [Chudnovsky algorithm](https://en.wikipedia.org/wiki/Chudnovsky_algorithm), one of the fastest known series for π. It converges at roughly **14.18 decimal digits per term**:

```
1/π = (12 / 640320^(3/2)) · Σ (-1)^k · (6k)! · (13591409 + 545140134·k)
                                         ─────────────────────────────────
                                              (3k)! · (k!)^3 · 640320^(3k)
```

Rather than evaluating terms one by one, the app uses **binary splitting** — a divide-and-conquer technique that groups the entire sum into a tree of subproblems. This avoids redundant work and keeps intermediate numbers as small as possible for as long as possible, which matters a lot when you're multiplying integers hundreds of thousands of digits long.

The final value is extracted using integer arithmetic throughout: the square root of 10005 is computed as an integer square root at high precision, and the decimal string is recovered by scaling everything by 10^n before inserting the decimal point.

No native libraries or NuGet packages are used — just `System.Numerics.BigInteger` from the .NET standard library.

### Adaptive Display

Computing and displaying are intentionally decoupled:

- A **background task** runs Chudnovsky continuously, doubling its chunk size each iteration (5k → 10k → 20k → … → 2M digits) and pushing results into an in-memory buffer.
- The **UI timer** (20 fps) drains the buffer at a controlled trickle rate so digits appear smoothly rather than in sudden jumps.

Every 600ms, a small feedback controller measures the actual compute rate and adjusts how many digits are shown per frame. If the buffer is growing faster than the display can drain it, the trickle speeds up; if the buffer runs thin, it slows down. A hard frame-time cap (8ms per tick) ensures the UI never feels sluggish regardless of how fast the underlying computation runs.

The result is that the app automatically runs as fast as your CPU allows while keeping the display smooth on any hardware.
