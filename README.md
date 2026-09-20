# ClipStudio

## What it does
ClipCreator processes videos from a local file or via a YouTubeDownloader into 9:16 vertical clips. A TranscriptionService using Whisper analyzes the audio. A Groq LLM then selects highlights (with a loudness/face heuristic fallback if needed), optionally presenting them in a review screen. The FaceTrackerService drives the final face-tracked render, which features stacked and blur-fill layouts along with optional filler-word removal.

## Requirements
- Windows
- .NET 8 SDK
- Build with `dotnet build ClipStudio/ClipStudio/ClipStudio.csproj`
- NVIDIA GPU acceleration requires the CUDA 12 runtime to be installed on the host machine.

## Setup
- `yt-dlp.exe` and `ffmpeg.exe` must go in the `Binaries` folder, which is copied to the output folder by the csproj.
- The Whisper model file `ggml-base.en.bin` belongs in the `Models` folder (`Models\ggml-base.en.bin`) and can be downloaded from the whisper.cpp model repository on Hugging Face.
- Note: The face-detection model downloads automatically on first use.

## Groq
To use the LLM selection, create an API key at https://console.groq.com/keys. Set it in PowerShell with `setx GROQ_API_KEY "gsk_..."` and then restart your terminal or IDE. Without a key, the app falls back to the heuristic method. The model ID constant is located in `GroqConfig`. If requests start failing with a decommissioned-model error, check https://console.groq.com/docs/deprecations for updates.

## Usage
Paste a URL or pick a file, choose your options, and click Start. After processing, a review screen allows for manual adjustments before rendering. The default output folder is `Videos\ClipStudio`.

## Troubleshooting
Common issues include a missing Whisper model or a missing Groq key. Additionally, non-ASCII characters in local source paths can prevent OpenCV from opening the video; when this happens, face tracking falls back to a simple center crop. Check the application log for further details.
