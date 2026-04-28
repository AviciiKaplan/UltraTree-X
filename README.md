UltraTree: High-Performance NTFS Volume Analyzer
UltraTree is a low-level storage analysis utility developed as a Computer Science Capstone project at Kean University. Designed for speed and forensic-level accuracy, it bypasses high-level Windows APIs to perform direct metadata extraction from the NTFS Master File Table (MFT).

🚀 Key Features
Direct Volume Access: Utilizes raw CreateFileW calls and Win32 P/Invoke signatures (kernel32.dll) to access volume data directly, bypassing the standard OS file system layer.

Two-Pass Aggregation Algorithm: Implements a proprietary logic to solve "fragmentation blindness" by strictly mapping $DATA attributes from fragmented extension records back to their primary base file entries.

Performance Engineering: Leverages Parallel.ForEach for multi-threaded MFT record parsing and ReadOnlySpan<byte> for high-speed, zero-allocation binary data processing.

Professional UI/UX: Features a "Deep Oceanic" professional aesthetic built in WPF, utilizing monochromatic slate and indigo palettes for high-contrast technical data display.

Dynamic Visualization: Integrated with LiveChartsCore and SkiaSharp to provide real-time doughnut charts and hierarchical tree views of storage distribution.

🛠 Technical Challenge: The "Ghost Data" Problem
Most raw MFT scanners suffer from "fragmentation blindness," where they misinterpret random binary noise as valid index records, leading to "ghost data" hallucinations (e.g., a 930 GB drive reporting 1.42 TB used).

UltraTree eliminates this issue by:

Strict Map Parsing: Following the MFT RunList to ensure only true index sectors are read.

Gap Analysis: Cross-verifying raw MFT sums against OS-reported allocated space to ensure 100% data parity (e.g., successfully reconciling massive 17 GB .ucas project chunks).

💻 Tech Stack
Language: C# / .NET

UI Framework: Windows Presentation Foundation (WPF) with MVVM Architecture

Graphics: SkiaSharp & LiveChartsCore

Low-Level: Win32 API (P/Invoke)

📝 Usage
UltraTree requires Administrative Privileges to obtain a raw handle to the physical drive volume.
