- Windows WPF app, net8.0-windows (EnableWindowsTargeting=true).
- Build: dotnet build ClipStudio/ClipStudio/ClipStudio.csproj
- You run on Linux: compile-only. You cannot run the app, ffmpeg, or yt-dlp.
  Never claim runtime behavior is verified.
- Never delete an existing code path until its replacement is wired in and builds.
- Only edit files listed in the approved plan.
- If you edit the same file twice for the same error, stop and report the blocker.
- Never hardcode secrets. The Groq key comes from the GROQ_API_KEY env var.
- In your final summary, paste `git diff --stat` and the lines implementing the task.
