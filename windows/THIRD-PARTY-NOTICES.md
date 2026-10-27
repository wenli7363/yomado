# Windows third-party notices

The Yomado application and managed libraries use the repository's MIT license.

The `driver` source directory, including its adapted frame-processing and IddCx
initialization, is distributed under MS-PL. Microsoft's original copyright and
full license are retained in `driver/LICENSE` and `driver/upstream/LICENSE`.
`driver/upstream/REVISION` pins the exact Microsoft Windows-driver-samples
revision used as a reference. The upstream sample files are retained for audit;
the build compiles `YomadoDriver.cpp` and `FrameProcessor.cpp`, not that sample.

Build tools and .NET runtime redistribution remain governed by their respective
Microsoft licenses. Build dependencies and generated packages are ignored by Git.
