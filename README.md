<div align="center">

# LiveCaptions Translator

### *Real-time speech translation tool based on Windows Live Captions*

[![Windows 11](https://img.shields.io/badge/platform-Windows%2011-blue?logo=windows&style=&color=1E9BFA)](https://www.microsoft.com/en-us/software-download/windows11)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/10.0)

**Idea inspired by [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator)** — this is an independent, from-scratch implementation in C#/WPF.

</div>

## Overview

**✨ LiveCaptions Translator = Windows Live Captions + any OpenAI-compatible LLM ✨**

This is a lightweight tool that seamlessly integrates an LLM translation endpoint with Windows Live Captions, enabling real-time speech translation without requiring a Copilot+ PC or any cloud service.

Windows' built-in Live Captions is easy to use, uses few resources, and has extremely high recognition accuracy. If you empower it with the awesome translation capabilities of LLMs — local (LM Studio, Ollama) or online (OpenRouter, OpenAI-compatible APIs) — you get a real-time translator that works offline if you want it to.

**🚀 Quick Start:** rename `appsettings.orig.json` to `appsettings.json`, set your LLM endpoint in it, then build (`dotnet run`) or launch the executable from `bin/Release` — and start translating!

## Features

- **🔄 Seamless Integration with Windows Live Captions**

  The app launches (or attaches to an already running) Windows Live Captions process in the background and reads its transcript via UI Automation — no separate window needed. Once the first captions appear, the Live Captions window is hidden automatically; a toolbar button brings it back any time you need to change settings.

  If the Live Captions process dies or loses its window, the app detects it and restarts it (throttled to avoid kill/launch storms).

- **🌐 Any OpenAI-Compatible LLM Endpoint**

  Works with anything that speaks the OpenAI chat-completions API: [LM Studio](https://lmstudio.ai) (fully local), [Ollama](https://ollama.com), [OpenRouter](https://openrouter.ai), or any self-hosted / online endpoint. You choose the model, the prompt, and the target language — all in `appsettings.json`.

  LLM-based translation is recommended: LLMs handle incomplete sentences gracefully and understand context, which is exactly what live captions produce.

- **📡 Streaming Translations**

  Responses are streamed token-by-token; partial translations appear on screen as soon as the first meaningful chunk arrives, so you read along with the speaker instead of waiting for full sentences.

- **🧠 Context-Aware Translation**

  The last N finalized lines (original + translation) are sent to the model as context, keeping terminology and tone consistent across a conversation.

- **⚙️ Robust by Design**

  - Model warm-up request before the main traffic starts, so loading a local model is not interrupted by cancellations
  - Retries with backoff for transient failures and per-request timeouts
  - Smart line matching: rephrased or extended captions update the same line instead of spawning duplicates; finalized lines are never silently left untranslated
  - Failed requests keep the previous translation on screen — nothing flickers away

- **🎛️ Flexible Controls**

  Pause / resume translation, always-on-top window, one-click transcript clearing, and a status bar showing both Live Captions and LLM state.

- **📝 Logging**

  Configurable log levels (Off / Warning / Info / Debug) written to daily files in the `logs/` folder next to the executable — handy for debugging endpoint issues.

## Configuration

All settings live in `appsettings.json` next to the executable:

| Setting                  | Default                          | Description                                                        |
|--------------------------|----------------------------------|--------------------------------------------------------------------|
| `ApiUrl`                 | `https://api.openai.com/v1`      | Base URL of an OpenAI-compatible endpoint.                         |
| `ApiKey`                 | *(empty)*                        | API key, if the endpoint requires one (not needed for LM Studio).  |
| `Model`                  | *(empty)*                        | Model name to request from the endpoint.                           |
| `Prompt`                 | professional interpreter prompt  | System prompt; `{0}` is replaced with the target language.         |
| `TargetLanguage`         | `ru-RU`                          | Language to translate into (inserted into the prompt).             |
| `ContextCount`           | `1`                              | How many previous lines are sent as context (0–2).                 |
| `VisibleLines`           | `10`                             | Maximum number of caption lines kept on screen.                    |
| `Streaming`              | `true`                           | Use streaming responses when the endpoint supports them.           |
| `Temperature`            | `0.3`                            | Sampling temperature for translations.                             |
| `MinSentenceLength`      | `10`                             | Active (non-final) lines shorter than this are not translated yet. |
| `DebounceMs`             | `1000`                           | Delay before translating a still-changing active line.             |
| `Topmost`                | `true`                           | Start with the always-on-top flag enabled.                         |
| `PollIntervalMs`         | `100`                            | How often the Live Captions transcript is polled.                  |
| `StreamFlushMs`          | `120`                            | Minimum interval between partial-translation screen updates.       |
| `RequestTimeoutSeconds`  | `120`                            | Per-request timeout (generous enough for local model loading).     |
| `RetryCount`             | `2`                              | Number of retries for transient request failures.                  |
| `MaxConcurrentRequests`  | `2`                              | Maximum number of in-flight translation requests.                  |
| `LogLevel`               | `Warning`                        | Log verbosity: `Off`, `Warning`, `Info`, or `Debug`.               |

## Getting Started

> ⚠️ **IMPORTANT:** You must complete the following steps before running LiveCaptions Translator for the first time.
>
> For detailed information, see Microsoft's guide on [Using live captions](https://support.microsoft.com/en-us/windows/use-live-captions-to-better-understand-audio-b52da59c-14b8-4031-aeeb-f6a47e6055df).

### Step 1: Verify Windows Live Captions Availability

Confirm Live Captions is available on your system using any of these methods:

- Toggle **Live captions** in the quick settings
- Press **Win + Ctrl + L**
- Navigate to **Settings** > **Accessibility** > **Captions** and enable **Live captions**

### Step 2: Configure Windows Live Captions

When you first start, Windows Live Captions will ask for your consent to process voice data on your device and prompt you to download language files used by on-device speech recognition.

After launching Windows Live Captions, select the source language and press **Start**. The app reads whatever transcript appears there — including microphone audio if you enable that option in its settings.

> ⚠️ **IMPORTANT:** You must set the correct source language in Windows Live Captions!

### Step 3: Point the App at Your LLM Endpoint

The repository ships with a template config `appsettings.orig.json` (safe defaults, no API key). Rename it to `appsettings.json` next to the executable and set your own connection settings — `ApiUrl`, `Model`, and (if needed) `ApiKey`:

```powershell
Copy-Item appsettings.orig.json appsettings.json
# then edit appsettings.json with your endpoint / model / API key
```

For a fully local setup, run [LM Studio](https://lmstudio.ai), load a model, start its local server (default: `http://localhost:11435/v1`), and use that URL.

### Step 4: Run

```powershell
dotnet run --project LiveCaptionsTranslator.csproj
```

or launch the built executable from `bin/Release/net10.0-windows/`. Speak (or play audio) — original captions appear on the left, translations stream in on the right. 🎉

## Building from Source

The project targets **.NET 10** and uses WPF:

```powershell
dotnet build LiveCaptionsTranslator.csproj   # build the app
dotnet test tests/LiveCaptionsTranslator.Tests.csproj   # run unit tests
```

## Project Structure

| Path                        | Purpose                                                        |
|-----------------------------|----------------------------------------------------------------|
| `engine/CaptionEngine.cs`   | Polls Live Captions, reconciles caption lines with the UI.     |
| `engine/LiveCaptionsService.cs` | Launches/attaches to Windows Live Captions and reads its transcript via UI Automation. |
| `engine/TranslationEngine.cs` | Queues, debounces, retries, and streams translation requests.  |
| `engine/OpenAIClient.cs`    | OpenAI-compatible chat-completions client with streaming and fallback request variants. |
| `models/AppSettings.cs`     | Settings model loaded from / saved to `appsettings.json`.      |
| `utils/`                    | Logging, text normalization, native window APIs.               |
| `tests/EngineTests.cs`      | Unit tests for transcript parsing and engine behavior.         |

## Credits

This project is an independent, from-scratch implementation. The idea of combining Windows Live Captions with LLM translation was inspired by [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator).
