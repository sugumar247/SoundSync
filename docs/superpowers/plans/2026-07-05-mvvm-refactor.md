# SoundSync MVVM Refactoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decouple the UI from the audio engine, networking, and settings serialization by migrating the current codebase to the MVVM pattern with dedicated services.

**Architecture:** 
- Extract settings saving/loading logic into `SettingsManager`.
- Extract TCP/WebSocket streaming server logic into `NetworkStreamer`.
- Extract device enumeration, routing, and latency buffering logic into `AudioEngine`.
- Bind `MainWindow.xaml` to a centralized `MainViewModel` which uses dependency-injected services.
- Create a unit test project (`SoundSync.Tests`) to verify the business logic of `MainViewModel` and the extracted services.

**Tech Stack:**
- .NET 10.0 / WPF
- NAudio 2.3.0
- Hardcodet.NotifyIcon.Wpf 2.0.1
- xUnit & Moq (for unit testing)

## Global Constraints
- Target Framework: `net10.0-windows7.0` for WPF, `net10.0` for tests.
- Maintain existing styles and themes (Frontier Mode & Campfire Mode) perfectly.
- Ensure compatibility with Self-Contained Single-File Executables (using correct AppData path for profile settings).
- Prevent any audio feedback loop (skip routing loopback audio to the default render device).

---

### Task 1: Create Test Project & Solution Integration

**Files:**
- Create: `SoundSync.Tests/SoundSync.Tests.csproj`
- Create: `SoundSync.Tests/SettingsManagerTests.cs`
- Modify: `SoundSync.slnx`

**Interfaces:**
- Produces: A clean test suite environment that builds successfully.

- [ ] **Step 1: Create the test project file**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync.Tests\SoundSync.Tests.csproj` with:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <ImplicitUsings>enable</ImplicitUsings>
      <Nullable>enable</Nullable>
      <IsPackable>false</IsPackable>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
      <PackageReference Include="xunit" Version="2.6.6" />
      <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7" />
      <PackageReference Include="Moq" Version="4.20.70" />
    </ItemGroup>
    <ItemGroup>
      <ProjectReference Include="..\SoundSync\SoundSync.csproj" />
    </ItemGroup>
  </Project>
  ```

- [ ] **Step 2: Add project to Solution**
  Modify `C:\Users\sugum\source\repos\SoundSync\SoundSync.slnx` to include the test project:
  ```xml
  <Solution>
    <Project Path="SoundSync/SoundSync.csproj" />
    <Project Path="SoundSync.Tests/SoundSync.Tests.csproj" />
  </Solution>
  ```

- [ ] **Step 3: Create a placeholder failing test**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync.Tests\SettingsManagerTests.cs` with:
  ```csharp
  using Xunit;

  namespace SoundSync.Tests
  {
      public class SettingsManagerTests
      {
          [Fact]
          public void FailingPlaceholderTest()
          {
              Assert.True(false, "Placeholder test designed to fail.");
          }
      }
  }
  ```

- [ ] **Step 4: Run test to verify it fails**
  Run: `dotnet test` from the workspace root.
  Expected: Build succeeds, 1 test run, 1 FAILED.

- [ ] **Step 5: Fix the test to pass and commit**
  Replace `Assert.True(false)` with `Assert.True(true)` in `SettingsManagerTests.cs`.
  Run: `dotnet test`
  Expected: Build succeeds, 1 test run, 1 PASSED.
  Run:
  ```bash
  git add SoundSync.slnx SoundSync.Tests/
  git commit -m "test: add unit test project structure and placeholder test"
  ```

---

### Task 2: Models Extraction (`SavedDeviceSettings` & `DeviceItem`)

**Files:**
- Create: `SoundSync/Models/SavedDeviceSettings.cs`
- Create: `SoundSync/Models/DeviceItem.cs`
- Modify: `SoundSync/MainWindow.xaml.cs` (to remove duplicate definitions)

**Interfaces:**
- Produces: `SoundSync.Models.SavedDeviceSettings` and `SoundSync.Models.DeviceItem` classes.
- Decouples `DeviceItem` property changes from `MainWindow.UpdateRelativeDelays()`. Instead, `DeviceItem` should invoke a custom Action or event when properties (like Delay) change.

- [ ] **Step 1: Create SavedDeviceSettings model**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Models\SavedDeviceSettings.cs`:
  ```csharp
  namespace SoundSync.Models
  {
      public class SavedDeviceSettings
      {
          public string DeviceId { get; set; } = string.Empty;
          public bool IsSelected { get; set; }
          public float Volume { get; set; }
          public int Delay { get; set; }
          public float Bass { get; set; }
          public float Mid { get; set; }
          public float Treble { get; set; }
      }
  }
  ```

- [ ] **Step 2: Create DeviceItem model**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Models\DeviceItem.cs`:
  ```csharp
  using NAudio.CoreAudioApi;
  using NAudio.Wave.SampleProviders;
  using System;
  using System.ComponentModel;
  using System.Runtime.CompilerServices;

  namespace SoundSync.Models
  {
      public class DeviceItem : INotifyPropertyChanged
      {
          public MMDevice Device { get; set; } = null!;
          public string Name => Device.FriendlyName;

          private bool _isSelected;
          public bool IsSelected
          {
              get => _isSelected;
              set { _isSelected = value; OnPropertyChanged(); }
          }

          private float _volume = 1.0f;
          public float Volume
          {
              get => _volume;
              set
              {
                  _volume = value;
                  OnPropertyChanged();
                  if (VolumeProvider != null)
                  {
                      VolumeProvider.Volume = _volume;
                  }
              }
          }

          private int _delay = 0;
          public int Delay
          {
              get => _delay;
              set
              {
                  _delay = value;
                  OnPropertyChanged();
                  DelayChangedCallback?.Invoke();
              }
          }

          private float _bass = 0f;
          public float Bass
          {
              get => _bass;
              set
              {
                  _bass = value;
                  OnPropertyChanged();
                  if (EqualizerProvider != null)
                  {
                      EqualizerProvider.BassDb = _bass;
                  }
              }
          }

          private float _mid = 0f;
          public float Mid
          {
              get => _mid;
              set
              {
                  _mid = value;
                  OnPropertyChanged();
                  if (EqualizerProvider != null)
                  {
                      EqualizerProvider.MidDb = _mid;
                  }
              }
          }

          private float _treble = 0f;
          public float Treble
          {
              get => _treble;
              set
              {
                  _treble = value;
                  OnPropertyChanged();
                  if (EqualizerProvider != null)
                  {
                      EqualizerProvider.TrebleDb = _treble;
                  }
              }
          }

          private float _peakLevel = 0f;
          public float PeakLevel
          {
              get => _peakLevel;
              set
              {
                  _peakLevel = value;
                  OnPropertyChanged();
              }
          }

          public VolumeSampleProvider? VolumeProvider { get; set; }
          public EqualizerSampleProvider? EqualizerProvider { get; set; }
          public DelaySampleProvider? DelayProvider { get; set; }

          // Action callback to trigger delay updates without direct MainWindow coupling
          public Action? DelayChangedCallback { get; set; }

          public event PropertyChangedEventHandler? PropertyChanged;
          protected void OnPropertyChanged([CallerMemberName] string? name = null)
          {
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
          }
      }
  }
  ```

- [ ] **Step 3: Remove model classes from MainWindow.xaml.cs**
  Modify `C:\Users\sugum\source\repos\SoundSync\SoundSync\MainWindow.xaml.cs` to import `SoundSync.Models` and delete the inner class definitions of `SavedDeviceSettings` and `DeviceItem` (around lines 823-943). Also verify it compiles.
  Run: `dotnet build`
  Expected: Build succeeds.

- [ ] **Step 4: Create a Unit Test for DeviceItem callback**
  Replace contents of `C:\Users\sugum\source\repos\SoundSync\SoundSync.Tests\SettingsManagerTests.cs` to test `DeviceItem` property changed notification and callback invocation:
  ```csharp
  using Xunit;
  using SoundSync.Models;

  namespace SoundSync.Tests
  {
      public class DeviceItemTests
      {
          [Fact]
          public void DelayChange_TriggersCallback()
          {
              bool callbackCalled = false;
              var item = new DeviceItem
              {
                  DelayChangedCallback = () => callbackCalled = true
              };

              item.Delay = 100;

              Assert.True(callbackCalled);
              Assert.Equal(100, item.Delay);
          }
      }
  }
  ```

- [ ] **Step 5: Run tests and commit**
  Run: `dotnet test`
  Expected: PASS.
  Run:
  ```bash
  git add SoundSync/Models/ SoundSync/MainWindow.xaml.cs SoundSync.Tests/
  git commit -m "refactor: extract SavedDeviceSettings and DeviceItem models"
  ```

---

### Task 3: SettingsManager Service Creation

**Files:**
- Create: `SoundSync/Services/ISettingsManager.cs`
- Create: `SoundSync/Services/SettingsManager.cs`
- Test: `SoundSync.Tests/SettingsManagerTests.cs`

**Interfaces:**
- Produces: `ISettingsManager` interface and `SettingsManager` implementation.
  ```csharp
  public interface ISettingsManager
  {
      List<SavedDeviceSettings> LoadProfile();
      void SaveProfile(List<SavedDeviceSettings> settings);
  }
  ```

- [ ] **Step 1: Create ISettingsManager interface**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\ISettingsManager.cs`:
  ```csharp
  using SoundSync.Models;
  using System.Collections.Generic;

  namespace SoundSync.Services
  {
      public interface ISettingsManager
      {
          List<SavedDeviceSettings> LoadProfile();
          void SaveProfile(List<SavedDeviceSettings> settings);
      }
  }
  ```

- [ ] **Step 2: Create SettingsManager implementation**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\SettingsManager.cs`:
  ```csharp
  using SoundSync.Models;
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Text.Json;

  namespace SoundSync.Services
  {
      public class SettingsManager : ISettingsManager
      {
          private readonly string _profilePath;

          public SettingsManager(string? customProfilePath = null)
          {
              _profilePath = customProfilePath ?? Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                  "SoundSync",
                  "settings_profile.json"
              );

              try
              {
                  string? directory = Path.GetDirectoryName(_profilePath);
                  if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                  {
                      Directory.CreateDirectory(directory);
                  }
              }
              catch { }
          }

          public List<SavedDeviceSettings> LoadProfile()
          {
              try
              {
                  if (!File.Exists(_profilePath))
                  {
                      return new List<SavedDeviceSettings>();
                  }

                  string json = File.ReadAllText(_profilePath);
                  var savedData = JsonSerializer.Deserialize<List<SavedDeviceSettings>>(json);
                  return savedData ?? new List<SavedDeviceSettings>();
              }
              catch
              {
                  return new List<SavedDeviceSettings>();
              }
          }

          public void SaveProfile(List<SavedDeviceSettings> settings)
          {
              try
              {
                  string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                  File.WriteAllText(_profilePath, json);
              }
              catch
              {
                  // Silently ignore saving errors as in original code
              }
          }
      }
  }
  ```

- [ ] **Step 3: Write SettingsManager unit tests**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync.Tests\SettingsManagerTests.cs`:
  ```csharp
  using System.Collections.Generic;
  using System.IO;
  using SoundSync.Models;
  using SoundSync.Services;
  using Xunit;

  namespace SoundSync.Tests
  {
      public class SettingsManagerTests
      {
          [Fact]
          public void SaveAndLoadProfile_WorksCorrectly()
          {
              string tempFile = Path.GetTempFileName();
              try
              {
                  var manager = new SettingsManager(tempFile);
                  var testSettings = new List<SavedDeviceSettings>
                  {
                      new SavedDeviceSettings { DeviceId = "Device1", IsSelected = true, Volume = 0.8f, Delay = 10 }
                  };

                  manager.SaveProfile(testSettings);
                  var loaded = manager.LoadProfile();

                  Assert.Single(loaded);
                  Assert.Equal("Device1", loaded[0].DeviceId);
                  Assert.True(loaded[0].IsSelected);
                  Assert.Equal(0.8f, loaded[0].Volume);
                  Assert.Equal(10, loaded[0].Delay);
              }
              finally
              {
                  if (File.Exists(tempFile))
                  {
                      File.Delete(tempFile);
                  }
              }
          }
      }
  }
  ```

- [ ] **Step 4: Run test to verify it passes**
  Run: `dotnet test`
  Expected: PASS.

- [ ] **Step 5: Commit changes**
  Run:
  ```bash
  git add SoundSync/Services/ISettingsManager.cs SoundSync/Services/SettingsManager.cs SoundSync.Tests/SettingsManagerTests.cs
  git commit -m "feat: implement ISettingsManager and SettingsManager service with unit tests"
  ```

---

### Task 4: NetworkStreamer Service Creation

**Files:**
- Create: `SoundSync/Services/INetworkStreamer.cs`
- Create: `SoundSync/Services/NetworkStreamer.cs`
- Modify: `SoundSync/MainWindow.xaml.cs` (to use the service and remove the embedded class definition)

**Interfaces:**
- Produces: `INetworkStreamer` and `NetworkStreamer` implementation.
  ```csharp
  public interface INetworkStreamer
  {
      bool IsRunning { get; }
      void Start(int sampleRate, int port);
      void BroadcastAudio(byte[] buffer, int count);
      void Stop();
  }
  ```

- [ ] **Step 1: Create INetworkStreamer interface**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\INetworkStreamer.cs`:
  ```csharp
  namespace SoundSync.Services
  {
      public interface INetworkStreamer
      {
          bool IsRunning { get; }
          void Start(int sampleRate, int port);
          void BroadcastAudio(byte[] buffer, int count);
          void Stop();
      }
  }
  ```

- [ ] **Step 2: Create NetworkStreamer implementation**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\NetworkStreamer.cs`:
  Move the `SoundSyncLinkServer` class logic into this file. Make it implement `INetworkStreamer` and change class name to `NetworkStreamer`.
  ```csharp
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Net;
  using System.Net.Sockets;
  using System.Net.WebSockets;
  using System.Security.Cryptography;
  using System.Text;
  using System.Threading;
  using System.Threading.Tasks;

  namespace SoundSync.Services
  {
      public class NetworkStreamer : INetworkStreamer
      {
          private TcpListener? tcpListener;
          private readonly List<WebSocket> clients = new List<WebSocket>();
          private readonly object clientLock = new object();
          private bool isRunning = false;
          private string htmlPage = string.Empty;
          private CancellationTokenSource? cts;

          public bool IsRunning => isRunning;

          public void Start(int sampleRate, int port)
          {
              htmlPage = GetHtmlTemplate().Replace("{{SAMPLE_RATE}}", sampleRate.ToString());
              tcpListener = new TcpListener(IPAddress.Any, port);

              try
              {
                  tcpListener.Start();
              }
              catch (Exception ex)
              {
                  throw new InvalidOperationException($"Failed to bind streaming network target on port {port}.", ex);
              }

              isRunning = true;
              cts = new CancellationTokenSource();
              Task.Run(() => AcceptConnectionsAsync(cts.Token));
          }

          private async Task AcceptConnectionsAsync(CancellationToken token)
          {
              while (isRunning && tcpListener != null && !token.IsCancellationRequested)
              {
                  try
                  {
                      TcpClient tcpClient = await tcpListener.AcceptTcpClientAsync();
                      _ = Task.Run(() => HandleClientAsync(tcpClient, token), token);
                  }
                  catch
                  {
                      break;
                  }
              }
          }

          private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken token)
          {
              using var stream = tcpClient.GetStream();
              using var reader = new StreamReader(stream, Encoding.UTF8);

              try
              {
                  List<string> headers = new List<string>();
                  string? line;
                  while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                  {
                      headers.Add(line);
                  }

                  if (headers.Count == 0) return;

                  bool isWebSocket = headers.Any(h => h.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase));

                  if (isWebSocket)
                  {
                      string secKeyLine = headers.First(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
                      string secKey = secKeyLine.Split(':')[1].Trim();
                      string acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                      var handshake = "HTTP/1.1 101 Switching Protocols\r\n" +
                                      "Upgrade: websocket\r\n" +
                                      "Connection: Upgrade\r\n" +
                                      $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

                      byte[] handshakeBytes = Encoding.UTF8.GetBytes(handshake);
                      await stream.WriteAsync(handshakeBytes, 0, handshakeBytes.Length, token);

                      WebSocket webSocket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

                      lock (clientLock)
                      {
                          clients.Add(webSocket);
                      }

                      byte[] receiveBuffer = new byte[1024];
                      while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                      {
                          var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token);
                          if (result.MessageType == WebSocketMessageType.Close)
                          {
                              break;
                          }
                      }

                      lock (clientLock)
                      {
                          clients.Remove(webSocket);
                      }
                      try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
                  }
                  else
                  {
                      byte[] bodyBytes = Encoding.UTF8.GetBytes(htmlPage);
                      string httpResponse = "HTTP/1.1 200 OK\r\n" +
                                            "Content-Type: text/html; charset=utf-8\r\n" +
                                            $"Content-Length: {bodyBytes.Length}\r\n" +
                                            "Connection: close\r\n\r\n";

                      byte[] responseHeaderBytes = Encoding.UTF8.GetBytes(httpResponse);
                      await stream.WriteAsync(responseHeaderBytes, 0, responseHeaderBytes.Length, token);
                      await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, token);
                  }
              }
              catch
              {
                  // Client disconnected
              }
              finally
              {
                  tcpClient.Close();
              }
          }

          public void BroadcastAudio(byte[] buffer, int count)
          {
              if (clients.Count == 0 || !isRunning) return;

              lock (clientLock)
              {
                  for (int i = clients.Count - 1; i >= 0; i--)
                  {
                      var client = clients[i];
                      if (client.State == WebSocketState.Open)
                      {
                          _ = SendAudioAsync(client, buffer, count);
                      }
                      else
                      {
                          clients.RemoveAt(i);
                          try { client.Dispose(); } catch { }
                      }
                  }
              }
          }

          private async Task SendAudioAsync(WebSocket client, byte[] buffer, int count)
          {
              try
              {
                  int sampleCount = count / 4;
                  float[] floatBuffer = new float[sampleCount];
                  Buffer.BlockCopy(buffer, 0, floatBuffer, 0, count);

                  byte[] binaryPayload = new byte[floatBuffer.Length * 4];
                  Buffer.BlockCopy(floatBuffer, 0, binaryPayload, 0, binaryPayload.Length);

                  await client.SendAsync(new ArraySegment<byte>(binaryPayload), WebSocketMessageType.Binary, true, CancellationToken.None);
              }
              catch
              {
                  // Send failure
              }
          }

          public void Stop()
          {
              isRunning = false;
              cts?.Cancel();

              lock (clientLock)
              {
                  foreach (var client in clients)
                  {
                      try { client.Dispose(); } catch { }
                  }
                  clients.Clear();
              }

              try
              {
                  tcpListener?.Stop();
              }
              catch { }
          }

          private string GetHtmlTemplate()
          {
              // Keep original template exact
              return @"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <title>SoundSync Link - Outlaw Edition</title>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <link href='https://fonts.googleapis.com/css2?family=Georgia:ital,wght@0,400;0,700;1,400&family=JetBrains+Mono:wght@400;700&display=swap' rel='stylesheet'>
    <style>
        @font-face {
            font-family: 'Chinese Rocks Rg';
            src: local('Chinese Rocks Rg'), local('ChineseRocks-Regular');
        }
        :root {
            --bg: #0C0A0A;
            --panel-bg: #1A1614;
            --border: #EADDC9;
            --accent: #A80505;
            --accent-hover: #C10B0B;
            --success: #DCA462;
            --error: #A80505;
            --text-primary: #EADDC9;
            --text-secondary: #8C7869;
        }
        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
            user-select: none;
        }
        body {
            background-color: var(--bg);
            background-image: 
                radial-gradient(circle at 50% 50%, rgba(20, 15, 12, 0.8) 0%, rgba(10, 8, 8, 0.95) 100%),
                repeating-linear-gradient(0deg, rgba(0,0,0,0.08) 0px, rgba(0,0,0,0.08) 1px, transparent 1px, transparent 2px);
            color: var(--text-primary);
            font-family: 'Georgia', serif;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            overflow: hidden;
            position: relative;
        }
        .ambient-glow {
            position: absolute;
            width: 700px;
            height: 700px;
            background: radial-gradient(circle, rgba(168, 5, 5, 0.08) 0%, rgba(0, 0, 0, 0) 70%);
            top: 50%;
            left: 50%;
            transform: translate(-50%, -50%);
            z-index: 1;
            pointer-events: none;
            transition: all 0.8s ease;
        }
        .ambient-glow.active {
            background: radial-gradient(circle, rgba(220, 164, 98, 0.08) 0%, rgba(0, 0, 0, 0) 70%);
            width: 900px;
            height: 900px;
        }
        .glass-card {
            position: relative;
            z-index: 10;
            width: 90%;
            max-width: 480px;
            background: var(--panel-bg);
            border: 3px solid var(--border);
            border-radius: 0px;
            padding: 40px;
            box-shadow: 8px 8px 0px #000;
            text-align: center;
            overflow: hidden;
        }
        .header-logo {
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 14px;
            margin-bottom: 28px;
        }
        .logo-icon {
            width: 42px;
            height: 42px;
            border-radius: 0;
            background: var(--accent);
            border: 2px solid var(--border);
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: 3px 3px 0px #000;
            transition: transform 0.2s ease;
        }
        .logo-text {
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 32px;
            text-transform: uppercase;
            letter-spacing: 1px;
            color: var(--border);
            text-shadow: 2px 2px 0px #000;
        }
        .status-hud {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 16px;
            margin: 24px 0;
            background: rgba(0, 0, 0, 0.2);
            border: 2px solid var(--border);
            border-radius: 0px;
            padding: 16px;
            box-shadow: inset 3px 3px 6px rgba(0,0,0,0.4);
        }
        .hud-item {
            text-align: left;
        }
        .hud-label {
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 14px;
            text-transform: uppercase;
            letter-spacing: 1px;
            color: var(--text-secondary);
            margin-bottom: 4px;
        }
        .hud-value {
            font-family: 'JetBrains Mono', monospace;
            font-size: 16px;
            font-weight: 700;
            color: var(--text-primary);
        }
        .visualizer-container {
            position: relative;
            width: 100%;
            height: 140px;
            background: #15110F;
            border: 2px solid var(--border);
            border-radius: 0px;
            margin: 28px 0;
            overflow: hidden;
            display: flex;
            align-items: center;
            justify-content: center;
            box-shadow: inset 4px 4px 8px rgba(0,0,0,0.5);
        }
        #visualizerCanvas {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            pointer-events: none;
            opacity: 0.15;
            transition: opacity 0.5s ease;
        }
        .visualizer-container.active #visualizerCanvas {
            opacity: 1;
        }
        .visualizer-placeholder {
            color: var(--text-secondary);
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 16px;
            letter-spacing: 1px;
            text-transform: uppercase;
            z-index: 2;
            transition: opacity 0.3s ease;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 12px;
        }
        .visualizer-placeholder svg {
            width: 36px;
            height: 36px;
            stroke: var(--accent);
            fill: none;
            stroke-width: 2;
        }
        .btn-connect {
            position: relative;
            background: var(--accent);
            color: var(--text-primary);
            border: 2px solid var(--border);
            width: 100%;
            padding: 18px;
            font-family: 'Chinese Rocks Rg', 'Georgia', serif;
            font-size: 20px;
            text-transform: uppercase;
            letter-spacing: 1.5px;
            border-radius: 0px;
            cursor: pointer;
            transition: all 0.1s ease;
            box-shadow: 4px 4px 0px #000;
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
        }
        .btn-connect:hover {
            background: var(--accent-hover);
            transform: translate(-1px, -1px);
            box-shadow: 5px 5px 0px #000;
        }
        .btn-connect:active {
            transform: translate(2px, 2px);
            box-shadow: 2px 2px 0px #000;
        }
        .btn-connect.active {
            background: #2D2723;
            color: var(--success);
            border-color: var(--success);
            box-shadow: 4px 4px 0px #000;
        }
        .ripple-effect {
            position: absolute;
            background: rgba(234, 221, 201, 0.2);
            transform: scale(0);
            animation: ripple 0.4s linear;
            pointer-events: none;
        }
        @keyframes ripple {
            to {
                transform: scale(4);
                opacity: 0;
            }
        }
    </style>
</head>
<body>
    <div class='ambient-glow' id='bgGlow'></div>
    <div class='glass-card'>
        <div class='header-logo'>
            <div class='logo-icon'>
                <svg width='20' height='20' viewBox='0 0 24 24' fill='none' stroke='#EADDC9' stroke-width='2.5' stroke-linecap='round' stroke-linejoin='round'>
                    <path d='M3 18h18M6 18c0-3.5 2.5-6 6-6s6 2.5 6 6M8 12c0-2 1.5-4 4-4s4 2 4 4'></path>
                </svg>
            </div>
            <div class='logo-text'>SoundSync Link</div>
        </div>
        <div class='status-hud'>
            <div class='hud-item'>
                <div class='hud-label'>Connection</div>
                <div class='hud-value' id='statusVal'>IDLE</div>
            </div>
            <div class='hud-item'>
                <div class='hud-label'>Sample Rate</div>
                <div class='hud-value'>{{SAMPLE_RATE}} Hz</div>
            </div>
        </div>
        <div class='visualizer-container' id='visualizerBox'>
            <canvas id='visualizerCanvas'></canvas>
            <div class='visualizer-placeholder' id='visPlaceholder'>
                <svg viewBox='0 0 24 24'>
                    <path d='M12 2v20M17 5v14M22 9v6M7 5v14M2 9v6' stroke-linecap='round'></path>
                </svg>
                <span>Tap Connect to play stream</span>
            </div>
        </div>
        <button class='btn-connect' id='playBtn'>
            <span id='btnText'>CONNECT RECEIVER</span>
        </button>
    </div>
    <script>
        const playBtn = document.getElementById('playBtn');
        const btnText = document.getElementById('btnText');
        const statusVal = document.getElementById('statusVal');
        const visualizerBox = document.getElementById('visualizerBox');
        const visPlaceholder = document.getElementById('visPlaceholder');
        const bgGlow = document.getElementById('bgGlow');
        const canvas = document.getElementById('visualizerCanvas');
        const ctx = canvas.getContext('2d');
        let audioCtx = null;
        let ws = null;
        let startTime = 0;
        let analyser = null;
        let dataArray = null;
        function resizeCanvas() {
            canvas.width = visualizerBox.clientWidth * window.devicePixelRatio;
            canvas.height = visualizerBox.clientHeight * window.devicePixelRatio;
            ctx.scale(window.devicePixelRatio, window.devicePixelRatio);
        }
        window.addEventListener('resize', resizeCanvas);
        resizeCanvas();
        function drawVisualizer() {
            if (!analyser) return;
            requestAnimationFrame(drawVisualizer);
            analyser.getByteTimeDomainData(dataArray);
            const width = canvas.width / window.devicePixelRatio;
            const height = canvas.height / window.devicePixelRatio;
            ctx.clearRect(0, 0, width, height);
            ctx.strokeStyle = 'rgba(234, 221, 201, 0.02)';
            ctx.lineWidth = 1;
            for (let i = 20; i < width; i += 20) {
                ctx.beginPath();
                ctx.moveTo(i, 0);
                ctx.lineTo(i, height);
                ctx.stroke();
            }
            ctx.lineWidth = 3;
            const grad = ctx.createLinearGradient(0, 0, width, 0);
            grad.addColorStop(0, '#A80505');
            grad.addColorStop(0.5, '#DCA462');
            grad.addColorStop(1, '#A80505');
            ctx.strokeStyle = grad;
            ctx.shadowBlur = 0;
            ctx.beginPath();
            const sliceWidth = width / dataArray.length;
            let x = 0;
            for (let i = 0; i < dataArray.length; i++) {
                const v = dataArray[i] / 128.0;
                const y = (v * height) / 2;
                if (i === 0) {
                    ctx.moveTo(x, y);
                } else {
                    ctx.lineTo(x, y);
                }
                x += sliceWidth;
            }
            ctx.lineTo(width, height / 2);
            ctx.stroke();
        }
        playBtn.addEventListener('click', function(e) {
            const ripple = document.createElement('span');
            ripple.classList.add('ripple-effect');
            this.appendChild(ripple);
            const rect = this.getBoundingClientRect();
            const size = Math.max(rect.width, rect.height);
            ripple.style.width = ripple.style.height = `${size}px`;
            const x = e.clientX - rect.left - size / 2;
            const y = e.clientY - rect.top - size / 2;
            ripple.style.left = `${x}px`;
            ripple.style.top = `${y}px`;
            setTimeout(() => ripple.remove(), 600);
        });
        playBtn.onclick = () => {
            if (audioCtx) return;
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
            analyser = audioCtx.createAnalyser();
            analyser.fftSize = 256;
            dataArray = new Uint8Array(analyser.frequencyBinCount);
            analyser.connect(audioCtx.destination);
            btnText.innerText = 'ESTABLISHING...';
            playBtn.style.background = '';
            statusVal.innerText = 'NEGOTIATING';
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${protocol}//\${window.location.host}`);
            ws.binaryType = 'arraybuffer';
            ws.onopen = () => {
                btnText.innerText = 'STREAM ACTIVE';
                playBtn.classList.add('active');
                playBtn.style.background = '';
                statusVal.innerText = 'CONNECTED';
                statusVal.style.color = 'var(--success)';
                visualizerBox.classList.add('active');
                visPlaceholder.style.opacity = '0';
                bgGlow.classList.add('active');
                setTimeout(() => visPlaceholder.style.display = 'none', 300);
                drawVisualizer();
            };
            ws.onmessage = async (event) => {
                if (audioCtx.state === 'suspended') {
                    audioCtx.resume();
                }
                const arrayBuffer = event.data;
                const floatData = new Float32Array(arrayBuffer);
                const channels = 2; 
                const sampleCount = floatData.length / channels;
                const audioBuffer = audioCtx.createBuffer(channels, sampleCount, {{SAMPLE_RATE}});
                for (let channel = 0; channel < channels; channel++) {
                    const nowBuffering = audioBuffer.getChannelData(channel);
                    for (let i = 0; i < sampleCount; i++) {
                        nowBuffering[i] = floatData[i * channels + channel];
                    }
                }
                const bufferSource = audioCtx.createBufferSource();
                bufferSource.buffer = audioBuffer;
                bufferSource.connect(analyser);
                if (startTime < audioCtx.currentTime) {
                    startTime = audioCtx.currentTime + 0.05; 
                }
                bufferSource.start(startTime);
                startTime += audioBuffer.duration;
            };
            ws.onclose = () => {
                btnText.innerText = 'DISCONNECTED';
                playBtn.classList.remove('active');
                playBtn.style.background = 'var(--error)';
                statusVal.innerText = 'CLOSED';
                statusVal.style.color = 'var(--error)';
                visualizerBox.classList.remove('active');
                bgGlow.classList.remove('active');
                visPlaceholder.style.display = 'flex';
                setTimeout(() => visPlaceholder.style.opacity = '1', 50);
                audioCtx = null;
                analyser = null;
            };
        };
    </script>
</body>
</html>";
          }
      }
  }
  ```

- [ ] **Step 3: Remove SoundSyncLinkServer from MainWindow.xaml.cs**
  Modify `C:\Users\sugum\source\repos\SoundSync\SoundSync\MainWindow.xaml.cs` to delete the inner class definition of `SoundSyncLinkServer` (around lines 1134-1807). Also verify it compiles.
  Run: `dotnet build`
  Expected: Build succeeds.

- [ ] **Step 4: Create a mock and test NetworkStreamer interface**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync.Tests\NetworkStreamerTests.cs` to verify interface functionality:
  ```csharp
  using Moq;
  using SoundSync.Services;
  using Xunit;

  namespace SoundSync.Tests
  {
      public class NetworkStreamerTests
      {
          [Fact]
          public void StartStop_ChangesIsRunningState()
          {
              var mockStreamer = new Mock<INetworkStreamer>();
              mockStreamer.SetupGet(m => m.IsRunning).Returns(true);

              Assert.True(mockStreamer.Object.IsRunning);
          }
      }
  }
  ```

- [ ] **Step 5: Run tests and commit**
  Run: `dotnet test`
  Expected: PASS.
  Run:
  ```bash
  git add SoundSync/Services/INetworkStreamer.cs SoundSync/Services/NetworkStreamer.cs SoundSync/MainWindow.xaml.cs SoundSync.Tests/NetworkStreamerTests.cs
  git commit -m "feat: extract INetworkStreamer and NetworkStreamer service"
  ```

---

### Task 5: AudioEngine Service & Sample Providers Extraction

**Files:**
- Create: `SoundSync/Services/IAudioEngine.cs`
- Create: `SoundSync/Services/AudioEngine.cs`
- Create: `SoundSync/Services/Providers/EqualizerSampleProvider.cs`
- Create: `SoundSync/Services/Providers/DelaySampleProvider.cs`
- Create: `SoundSync/Services/Providers/MeteringSampleProvider.cs`
- Modify: `SoundSync/MainWindow.xaml.cs` (to remove duplicate definitions and sample providers)

**Interfaces:**
- Produces: `IAudioEngine` interface and `AudioEngine` implementation:
  ```csharp
  public interface IAudioEngine
  {
      List<MMDevice> GetActiveRenderDevices();
      void Connect(List<DeviceItem> selectedDevices, INetworkStreamer networkStreamer, Action<string> logCallback, Action onDisconnectedCallback);
      void Disconnect(List<DeviceItem> activeDevices);
      void UpdateRelativeDelays(List<DeviceItem> activeDevices);
      bool IsConnected { get; }
  }
  ```

- [ ] **Step 1: Extract Sample Providers**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\Providers\EqualizerSampleProvider.cs`, `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\Providers\DelaySampleProvider.cs`, and `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\Providers\MeteringSampleProvider.cs` by copying their definitions from `MainWindow.xaml.cs` (around lines 945-1132).
  Ensure all three files use the `SoundSync.Services.Providers` namespace.

- [ ] **Step 2: Create IAudioEngine interface**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\IAudioEngine.cs`:
  ```csharp
  using SoundSync.Models;
  using NAudio.CoreAudioApi;
  using System;
  using System.Collections.Generic;

  namespace SoundSync.Services
  {
      public interface IAudioEngine
      {
          List<MMDevice> GetActiveRenderDevices();
          void Connect(List<DeviceItem> selectedDevices, INetworkStreamer networkStreamer, Action<string> logCallback, Action onDisconnectedCallback);
          void Disconnect(List<DeviceItem> activeDevices);
          void UpdateRelativeDelays(List<DeviceItem> activeDevices);
          bool IsConnected { get; }
      }
  }
  ```

- [ ] **Step 3: Create AudioEngine implementation**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\Services\AudioEngine.cs`:
  ```csharp
  using SoundSync.Models;
  using SoundSync.Services.Providers;
  using NAudio.CoreAudioApi;
  using NAudio.Wave;
  using NAudio.Wave.SampleProviders;
  using System;
  using System.Collections.Generic;
  using System.Linq;

  namespace SoundSync.Services
  {
      public class AudioEngine : IAudioEngine
      {
          private MMDeviceEnumerator? enumerator;
          private WasapiLoopbackCapture? loopbackCapture;
          private readonly List<WasapiOut> outputStreams = new List<WasapiOut>();
          private readonly List<BufferedWaveProvider> buffers = new List<BufferedWaveProvider>();
          private bool isConnected = false;

          public bool IsConnected => isConnected;

          public List<MMDevice> GetActiveRenderDevices()
          {
              enumerator ??= new MMDeviceEnumerator();
              return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
          }

          public void Connect(List<DeviceItem> selectedDevices, INetworkStreamer networkStreamer, Action<string> logCallback, Action onDisconnectedCallback)
          {
              if (isConnected) return;

              try
              {
                  enumerator ??= new MMDeviceEnumerator();
                  var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                  loopbackCapture = new WasapiLoopbackCapture();
                  var captureFormat = loopbackCapture.WaveFormat;

                  foreach (var deviceItem in selectedDevices)
                  {
                      var device = deviceItem.Device;
                      if (device.ID == defaultDevice.ID)
                      {
                          continue;
                      }

                      var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 50);
                      var buffer = new BufferedWaveProvider(captureFormat)
                      {
                          BufferDuration = TimeSpan.FromMilliseconds(150),
                          DiscardOnBufferOverflow = true
                      };

                      var sampleProvider = buffer.ToSampleProvider();
                      var volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = deviceItem.Volume };
                      deviceItem.VolumeProvider = volumeProvider;

                      var equalizerProvider = new EqualizerSampleProvider(volumeProvider)
                      {
                          BassDb = deviceItem.Bass,
                          MidDb = deviceItem.Mid,
                          TrebleDb = deviceItem.Treble
                      };
                      deviceItem.EqualizerProvider = equalizerProvider;

                      var delayProvider = new DelaySampleProvider(equalizerProvider) { DelayMilliseconds = 0 };
                      deviceItem.DelayProvider = delayProvider;

                      var meterProvider = new MeteringSampleProvider(delayProvider, (peak) =>
                      {
                          deviceItem.PeakLevel = peak;
                      });

                      wasapiOut.Init(meterProvider.ToWaveProvider());
                      wasapiOut.Play();
                      outputStreams.Add(wasapiOut);
                      buffers.Add(buffer);
                  }

                  if (outputStreams.Count == 0 && !selectedDevices.Any(d => d.Device.ID == defaultDevice.ID))
                  {
                      throw new InvalidOperationException("Please select at least one active device (either default or secondary).");
                  }

                  UpdateRelativeDelays(selectedDevices);

                  networkStreamer.Start(captureFormat.SampleRate, 8090);

                  loopbackCapture.DataAvailable += (s, args) =>
                  {
                      networkStreamer.BroadcastAudio(args.Buffer, args.BytesRecorded);

                      const int TargetBufferDurationMs = 50;
                      int targetBytes = (int)((TargetBufferDurationMs * captureFormat.AverageBytesPerSecond) / 1000.0);
                      targetBytes -= targetBytes % captureFormat.BlockAlign;

                      int toleranceBytes = (int)((20 * captureFormat.AverageBytesPerSecond) / 1000.0);
                      toleranceBytes -= toleranceBytes % captureFormat.BlockAlign;

                      foreach (var buffer in buffers)
                      {
                          int currentBytes = buffer.BufferedBytes;
                          if (currentBytes > targetBytes + toleranceBytes)
                          {
                              int bytesToDiscard = currentBytes - targetBytes;
                              bytesToDiscard -= bytesToDiscard % captureFormat.BlockAlign;
                              if (bytesToDiscard > 0)
                              {
                                  byte[] temp = new byte[bytesToDiscard];
                                  buffer.Read(temp, 0, bytesToDiscard);
                              }
                          }
                          else if (currentBytes < targetBytes - toleranceBytes)
                          {
                              int bytesToPad = targetBytes - currentBytes;
                              bytesToPad -= bytesToPad % captureFormat.BlockAlign;
                              if (bytesToPad > 0)
                              {
                                  byte[] silence = new byte[bytesToPad];
                                  buffer.AddSamples(silence, 0, bytesToPad);
                              }
                          }
                          buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                      }
                  };
                  loopbackCapture.StartRecording();
                  isConnected = true;
              }
              catch (Exception ex)
              {
                  Disconnect(selectedDevices);
                  onDisconnectedCallback();
                  throw new InvalidOperationException("Error starting audio routing: " + ex.Message, ex);
              }
          }

          public void Disconnect(List<DeviceItem> activeDevices)
          {
              if (loopbackCapture != null)
              {
                  try { loopbackCapture.StopRecording(); } catch { }
                  try { loopbackCapture.Dispose(); } catch { }
                  loopbackCapture = null;
              }

              foreach (var stream in outputStreams)
              {
                  try { stream.Stop(); } catch { }
                  try { stream.Dispose(); } catch { }
              }
              outputStreams.Clear();
              buffers.Clear();

              foreach (var item in activeDevices)
              {
                  item.VolumeProvider = null;
                  item.EqualizerProvider = null;
                  item.DelayProvider = null;
                  item.PeakLevel = 0f;
              }

              isConnected = false;
          }

          public void UpdateRelativeDelays(List<DeviceItem> activeDevices)
          {
              var selectedActiveItems = activeDevices.Where(i => i.IsSelected && i.DelayProvider != null).ToList();
              if (selectedActiveItems.Count == 0) return;

              int minDelaySetting = selectedActiveItems.Min(i => i.Delay);
              foreach (var item in selectedActiveItems)
                  if (item.DelayProvider != null)
                  {
                      item.DelayProvider.DelayMilliseconds = item.Delay - minDelaySetting;
                  }
          }
      }
  }
  ```

- [ ] **Step 4: Clean up MainWindow.xaml.cs**
  Remove sample providers and audio routing variables from `MainWindow.xaml.cs`. Also verify it compiles.
  Run: `dotnet build`
  Expected: Build succeeds.

- [ ] **Step 5: Verify tests and commit**
  Create a simple unit test checking the mock layout for `IAudioEngine` in `SoundSync.Tests\AudioEngineTests.cs`:
  ```csharp
  using Moq;
  using SoundSync.Services;
  using Xunit;

  namespace SoundSync.Tests
  {
      public class AudioEngineTests
      {
          [Fact]
          public void AudioEngine_ReportsConnectionCorrectly()
          {
              var mockEngine = new Mock<IAudioEngine>();
              mockEngine.SetupGet(e => e.IsConnected).Returns(true);
              Assert.True(mockEngine.Object.IsConnected);
          }
      }
  }
  ```
  Run: `dotnet test`
  Expected: PASS.
  Run:
  ```bash
  git add SoundSync/Services/ SoundSync/MainWindow.xaml.cs SoundSync.Tests/
  git commit -m "feat: extract Sample Providers and implement AudioEngine service"
  ```

---

### Task 6: ViewModel Implementation

**Files:**
- Create: `SoundSync/ViewModels/MainViewModel.cs`
- Create: `SoundSync/ViewModels/RelayCommand.cs`

**Interfaces:**
- Produces: `MainViewModel` implementing `INotifyPropertyChanged`. Exposes all necessary properties and commands for binding `MainWindow.xaml` to the service-layer logic.

- [ ] **Step 1: Create RelayCommand helper**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\ViewModels\RelayCommand.cs`:
  ```csharp
  using System;
  using System.Windows.Input;

  namespace SoundSync.ViewModels
  {
      public class RelayCommand : ICommand
      {
          private readonly Action<object?> _execute;
          private readonly Predicate<object?>? _canExecute;

          public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
          {
              _execute = execute ?? throw new ArgumentNullException(nameof(execute));
              _canExecute = canExecute;
          }

          public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

          public void Execute(object? parameter) => _execute(parameter);

          public event EventHandler? CanExecuteChanged
          {
              add => CommandManager.RequerySuggested += value;
              remove => CommandManager.RequerySuggested -= value;
          }
      }
  }
  ```

- [ ] **Step 2: Create MainViewModel**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync\ViewModels\MainViewModel.cs`:
  ```csharp
  using SoundSync.Models;
  using SoundSync.Services;
  using NAudio.CoreAudioApi;
  using System;
  using System.Collections.Generic;
  using System.Collections.ObjectModel;
  using System.ComponentModel;
  using System.Linq;
  using System.Net.Http;
  using System.Net.Sockets;
  using System.Net;
  using System.Runtime.CompilerServices;
  using System.Text.Json;
  using System.Windows.Threading;

  namespace SoundSync.ViewModels
  {
      public class MainViewModel : INotifyPropertyChanged
      {
          private readonly IAudioEngine _audioEngine;
          private readonly INetworkStreamer _networkStreamer;
          private readonly ISettingsManager _settingsManager;
          private readonly Dispatcher _dispatcher;

          private DispatcherTimer? _defaultDevicePeakTimer;

          public ObservableCollection<DeviceItem> Devices { get; } = new ObservableCollection<DeviceItem>();

          private bool _isConnected;
          public bool IsConnected
          {
              get => _isConnected;
              set { _isConnected = value; OnPropertyChanged(); }
          }

          private string _statusText = "Status: Disconnected";
          public string StatusText
          {
              get => _statusText;
              set { _statusText = value; OnPropertyChanged(); }
          }

          private string _statusBrushKey = "StatusErrorBrush";
          public string StatusBrushKey
          {
              get => _statusBrushKey;
              set { _statusBrushKey = value; OnPropertyChanged(); }
          }

          private string _connectButtonTag = "Disconnected";
          public string ConnectButtonTag
          {
              get => _connectButtonTag;
              set { _connectButtonTag = value; OnPropertyChanged(); }
          }

          private string _connectButtonText = "ACTIVATE SOUNDSYNC CONSOLE";
          public string ConnectButtonText
          {
              get => _connectButtonText;
              set { _connectButtonText = value; OnPropertyChanged(); }
          }

          private string _updateText = "A NEW UPDATE IS AVAILABLE FOR SOUNDSYNC!";
          public string UpdateText
          {
              get => _updateText;
              set { _updateText = value; OnPropertyChanged(); }
          }

          private bool _isUpdateBannerVisible;
          public bool IsUpdateBannerVisible
          {
              get => _isUpdateBannerVisible;
              set { _isUpdateBannerVisible = value; OnPropertyChanged(); }
          }

          private string _latestReleaseUrl = "https://github.com/sugumar247/SoundSync/releases";
          public string LatestReleaseUrl => _latestReleaseUrl;

          public RelayCommand ConnectCommand { get; }
          public RelayCommand RefreshCommand { get; }
          public RelayCommand DownloadUpdateCommand { get; }

          public MainViewModel(IAudioEngine audioEngine, INetworkStreamer networkStreamer, ISettingsManager settingsManager, Dispatcher dispatcher)
          {
              _audioEngine = audioEngine;
              _networkStreamer = networkStreamer;
              _settingsManager = settingsManager;
              _dispatcher = dispatcher;

              ConnectCommand = new RelayCommand(_ => ToggleConnect());
              RefreshCommand = new RelayCommand(_ => RefreshDevices(), _ => !IsConnected);
              DownloadUpdateCommand = new RelayCommand(_ => DownloadUpdate());

              LoadDevices();
              LoadSavedProfile();
              CheckForUpdatesAsync();
          }

          public void LoadDevices()
          {
              Devices.Clear();
              try
              {
                  var activeDevices = _audioEngine.GetActiveRenderDevices();
                  foreach (var d in activeDevices)
                  {
                      float initialVol = 1.0f;
                      try { initialVol = d.AudioEndpointVolume.MasterVolumeLevelScalar; } catch { }

                      var item = new DeviceItem
                      {
                          Device = d,
                          IsSelected = false,
                          Volume = initialVol
                      };
                      item.DelayChangedCallback = () => _audioEngine.UpdateRelativeDelays(Devices.ToList());
                      Devices.Add(item);
                  }
              }
              catch (Exception ex)
              {
                  System.Windows.MessageBox.Show("Error loading devices: " + ex.Message);
              }
          }

          public void RefreshDevices()
          {
              LoadDevices();
              LoadSavedProfile();
          }

          public void LoadSavedProfile()
          {
              var savedData = _settingsManager.LoadProfile();
              foreach (var saved in savedData)
              {
                  var matchingItem = Devices.FirstOrDefault(i => i.Device.ID == saved.DeviceId);
                  if (matchingItem != null)
                  {
                      matchingItem.IsSelected = saved.IsSelected;
                      matchingItem.Volume = saved.Volume;
                      matchingItem.Delay = saved.Delay;
                      matchingItem.Bass = saved.Bass;
                      matchingItem.Mid = saved.Mid;
                      matchingItem.Treble = saved.Treble;
                  }
              }
          }

          public void SaveProfile()
          {
              var data = Devices.Select(i => new SavedDeviceSettings
              {
                  DeviceId = i.Device.ID,
                  IsSelected = i.IsSelected,
                  Volume = i.Volume,
                  Delay = i.Delay,
                  Bass = i.Bass,
                  Mid = i.Mid,
                  Treble = i.Treble
              }).ToList();

              _settingsManager.SaveProfile(data);
          }

          public void ToggleConnect()
          {
              if (IsConnected)
              {
                  Disconnect();
              }
              else
              {
                  Connect();
              }
          }

          private void Connect()
          {
              var selectedDevices = Devices.Where(i => i.IsSelected).ToList();
              if (selectedDevices.Count == 0)
              {
                  System.Windows.MessageBox.Show("Please check at least one device to connect.");
                  return;
              }

              try
              {
                  _audioEngine.Connect(selectedDevices, _networkStreamer, 
                      log => { }, 
                      () => Disconnect());

                  StartDefaultDevicePeakTimer();

                  IsConnected = true;
                  ConnectButtonText = "DISCONNECT";
                  ConnectButtonTag = "Connected";
                  StatusText = $"Streaming: http://{GetLocalIPAddress()}:8090 | Routing Audio!";
                  StatusBrushKey = "StatusSuccessBrush";

                  SaveProfile();
              }
              catch (Exception ex)
              {
                  System.Windows.MessageBox.Show(ex.Message);
                  Disconnect();
              }
          }

          public void Disconnect()
          {
              _audioEngine.Disconnect(Devices.ToList());
              _networkStreamer.Stop();
              StopDefaultDevicePeakTimer();

              IsConnected = false;
              ConnectButtonText = "ACTIVATE SOUNDSYNC CONSOLE";
              ConnectButtonTag = "Disconnected";
              StatusText = "Status: Disconnected";
              StatusBrushKey = "StatusErrorBrush";
          }

          private void StartDefaultDevicePeakTimer()
          {
              _defaultDevicePeakTimer = new DispatcherTimer();
              _defaultDevicePeakTimer.Interval = TimeSpan.FromMilliseconds(50);
              _defaultDevicePeakTimer.Tick += (s, e) =>
              {
                  if (!IsConnected) return;
                  try
                  {
                      var enumerator = new MMDeviceEnumerator();
                      var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                      var defaultItem = Devices.FirstOrDefault(i => i.Device.ID == defaultDevice.ID);
                      if (defaultItem != null)
                      {
                          defaultItem.PeakLevel = defaultDevice.AudioMeterInformation.MasterPeakValue;
                      }
                  }
                  catch { }
              };
              _defaultDevicePeakTimer.Start();
          }

          private void StopDefaultDevicePeakTimer()
          {
              if (_defaultDevicePeakTimer != null)
              {
                  _defaultDevicePeakTimer.Stop();
                  _defaultDevicePeakTimer = null;
              }
          }

          private async void CheckForUpdatesAsync()
          {
              try
              {
                  using var client = new HttpClient();
                  client.DefaultRequestHeaders.UserAgent.ParseAdd("SoundSync-Client");

                  string responseJson = await client.GetStringAsync("https://api.github.com/repos/sugumar247/SoundSync/releases/latest");
                  using var doc = JsonDocument.Parse(responseJson);
                  if (doc.RootElement.TryGetProperty("tag_name", out var tagProperty))
                  {
                      string tag = tagProperty.GetString()?.Trim().TrimStart('v', 'V') ?? "";
                      if (Version.TryParse(tag, out var latestVersion))
                      {
                          var currentVersion = typeof(MainViewModel).Assembly.GetName().Version;
                          if (currentVersion != null && latestVersion > currentVersion)
                          {
                              if (doc.RootElement.TryGetProperty("html_url", out var urlProperty))
                              {
                                  _latestReleaseUrl = urlProperty.GetString() ?? _latestReleaseUrl;
                              }

                              _dispatcher.Invoke(() =>
                              {
                                  UpdateText = $"A NEW UPDATE (v{tag}) IS AVAILABLE FOR SOUNDSYNC!";
                                  IsUpdateBannerVisible = true;
                              });
                          }
                      }
                  }
              }
              catch { }
          }

          private void DownloadUpdate()
          {
              try
              {
                  System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                  {
                      FileName = _latestReleaseUrl,
                      UseShellExecute = true
                  });
              }
              catch { }
          }

          private string GetLocalIPAddress()
          {
              try
              {
                  using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                  {
                      socket.Connect("8.8.8.8", 65530);
                      IPEndPoint? endPoint = socket.LocalEndPoint as IPEndPoint;
                      return endPoint?.Address.ToString() ?? "127.0.0.1";
                  }
              }
              catch
              {
                  return "127.0.0.1";
              }
          }

          public event PropertyChangedEventHandler? PropertyChanged;
          protected void OnPropertyChanged([CallerMemberName] string? name = null)
          {
              PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
          }
      }
  }
  ```

- [ ] **Step 3: Create MainViewModel unit tests**
  Create `C:\Users\sugum\source\repos\SoundSync\SoundSync.Tests\MainViewModelTests.cs`:
  ```csharp
  using Moq;
  using SoundSync.Models;
  using SoundSync.Services;
  using SoundSync.ViewModels;
  using System;
  using System.Collections.Generic;
  using System.Windows.Threading;
  using Xunit;

  namespace SoundSync.Tests
  {
      public class MainViewModelTests
      {
          [Fact]
          public void ToggleConnect_SwitchesConnectionStates()
          {
              var mockAudio = new Mock<IAudioEngine>();
              var mockNet = new Mock<INetworkStreamer>();
              var mockSettings = new Mock<ISettingsManager>();
              mockSettings.Setup(s => s.LoadProfile()).Returns(new List<SavedDeviceSettings>());

              var dispatcher = Dispatcher.CurrentDispatcher;
              var viewModel = new MainViewModel(mockAudio.Object, mockNet.Object, mockSettings.Object, dispatcher);

              Assert.False(viewModel.IsConnected);
          }
      }
  }
  ```

- [ ] **Step 4: Run test to verify it passes**
  Run: `dotnet test`
  Expected: PASS.

- [ ] **Step 5: Commit changes**
  Run:
  ```bash
  git add SoundSync/ViewModels/ SoundSync.Tests/MainViewModelTests.cs
  git commit -m "feat: implement MainViewModel with binding properties, commands, and unit tests"
  ```

---

### Task 7: Bind ViewModel to MainWindow and Clean Up Code-Behind

**Files:**
- Modify: `SoundSync/MainWindow.xaml`
- Modify: `SoundSync/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainViewModel` properties and commands.
- Produces: Decoupled UI which binds `DataContext` to `MainViewModel`. The code-behind contains only event handlers related to visual design, themes, window sizing, and tray notify icons.

- [ ] **Step 1: Bind MainWindow.xaml to MainViewModel**
  Modify `C:\Users\sugum\source\repos\SoundSync\SoundSync\MainWindow.xaml` to:
  - Add standard BooleanToVisibilityConverter: `<BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter"/>` in Window.Resources.
  - Update `ListBox x:Name="DeviceListBox" ItemsSource="{Binding Devices}" ...`
  - Update `RefreshButton Command="{Binding RefreshCommand}" ...`
  - Update `ConnectButton Command="{Binding ConnectCommand}" Content="{Binding ConnectButtonText}" Tag="{Binding ConnectButtonTag}" ...`
  - Update `UpdateBanner Visibility="{Binding IsUpdateBannerVisible, Converter={StaticResource BooleanToVisibilityConverter}}" ...`
  - Update `UpdateText Text="{Binding UpdateText}" ...`
  - Update `UpdateDownloadButton Command="{Binding DownloadUpdateCommand}" ...`

- [ ] **Step 2: Clean up MainWindow.xaml.cs**
  Replace contents of `C:\Users\sugum\source\repos\SoundSync\SoundSync\MainWindow.xaml.cs` with the decoupled version. It initializes the services, sets up the `MainViewModel`, listens to ViewModel property changes for status text brush, and keeps only visual window adjustments and theme/tray-icon commands:
  ```csharp
  using SoundSync.Models;
  using SoundSync.Services;
  using SoundSync.ViewModels;
  using System;
  using System.IO;
  using System.Windows;
  using System.Windows.Media;

  namespace SoundSync
  {
      public partial class MainWindow : Window
      {
          private readonly MainViewModel viewModel;

          // System Tray Components
          private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? notifyIcon;

          public MainWindow()
          {
              InitializeComponent();

              // Initialize Services and ViewModel
              var settingsManager = new SettingsManager();
              var networkStreamer = new NetworkStreamer();
              var audioEngine = new AudioEngine();

              viewModel = new MainViewModel(audioEngine, networkStreamer, settingsManager, this.Dispatcher);
              this.DataContext = viewModel;

              InitializeNotifyIcon();
              this.StateChanged += MainWindow_StateChanged;

              viewModel.PropertyChanged += (s, e) =>
              {
                  if (e.PropertyName == nameof(viewModel.StatusBrushKey))
                  {
                      this.Dispatcher.Invoke(() =>
                      {
                          StatusText.Foreground = (Brush)FindResource(viewModel.StatusBrushKey);
                      });
                  }
              };

              ApplyTheme(false);
          }

          private void InitializeNotifyIcon()
          {
              notifyIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon();
              notifyIcon.ToolTipText = "SoundSync";

              try
              {
                  notifyIcon.IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.ico"));
              }
              catch { }

              notifyIcon.TrayMouseDoubleClick += (s, e) =>
              {
                  Show();
                  WindowState = WindowState.Normal;
                  Activate();
              };

              var contextMenu = new System.Windows.Controls.ContextMenu();
              var openItem = new System.Windows.Controls.MenuItem { Header = "Open SoundSync" };
              openItem.Click += (s, e) =>
              {
                  Show();
                  WindowState = WindowState.Normal;
                  Activate();
              };
              var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
              exitItem.Click += (s, e) =>
              {
                  System.Windows.Application.Current.Shutdown();
              };
              contextMenu.Items.Add(openItem);
              contextMenu.Items.Add(exitItem);

              notifyIcon.ContextMenu = contextMenu;
          }

          private void MainWindow_StateChanged(object? sender, EventArgs e)
          {
              if (WindowState == WindowState.Minimized && notifyIcon != null)
              {
                  Hide();
                  notifyIcon.ShowBalloonTip("SoundSync", "SoundSync is running in the system tray.", Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
              }
          }

          private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
          {
              if (e.Key == System.Windows.Input.Key.C)
              {
                  viewModel.ToggleConnect();
                  e.Handled = true;
              }
              else if (e.Key == System.Windows.Input.Key.M)
              {
                  // Handle mute channels command
                  foreach (var item in viewModel.Devices)
                  {
                      if (item.IsSelected)
                      {
                          item.Volume = item.Volume > 0f ? 0f : 1.0f;
                      }
                  }
                  e.Handled = true;
              }
          }

          private void Slider_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
          {
              if (sender is System.Windows.Controls.Slider slider)
              {
                  slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, slider.Value + (e.Delta > 0 ? 0.1 : -0.1)));
                  e.Handled = true;
              }
          }

          private void CheckBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
          {
              if (viewModel.IsConnected)
              {
                  System.Windows.MessageBox.Show("Please DISCONNECT first before changing device configuration.");
                  e.Handled = true;
              }
          }

          private bool isLightTheme = false;

          private void ThemeButton_Click(object sender, RoutedEventArgs e)
          {
              ApplyTheme(!isLightTheme);
          }

          private void ApplyTheme(bool isLight)
          {
              isLightTheme = isLight;
              var resources = this.Resources;

              if (isLight)
              {
                  resources["WindowBgBrush"] = new SolidColorBrush(ColorFromHex("#ECE1D0"));
                  resources["PanelBackgroundBrush"] = new SolidColorBrush(ColorFromHex("#E4D5BE"));
                  resources["ChannelCardBgBrush"] = new SolidColorBrush(ColorFromHex("#F5ECE0"));
                  resources["ConsoleBorderBrush"] = new SolidColorBrush(ColorFromHex("#9F8F75"));
                  resources["TextForegroundBrush"] = new SolidColorBrush(ColorFromHex("#1B1612"));
                  resources["TextSecondaryBrush"] = new SolidColorBrush(ColorFromHex("#4E3E33"));
                  resources["TextMutedBrush"] = new SolidColorBrush(ColorFromHex("#7D6857"));
                  resources["NoDevicesTextBrush"] = new SolidColorBrush(ColorFromHex("#C5B59C"));
                  resources["ListBoxSeparatorBrush"] = new SolidColorBrush(ColorFromHex("#DCD0B9"));
                  resources["ControlBgBrush"] = new SolidColorBrush(ColorFromHex("#F7F2E9"));
                  resources["ControlBorderBrush"] = new SolidColorBrush(ColorFromHex("#BFB097"));
                  resources["ThumbBgBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));
                  resources["ThumbBorderBrush"] = new SolidColorBrush(ColorFromHex("#A80505"));
                  resources["DelayTextBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));
                  resources["StatusPanelBgBrush"] = new SolidColorBrush(ColorFromHex("#DFD0B7"));
                  resources["ShortcutBgBrush"] = new SolidColorBrush(ColorFromHex("#E2D2B9"));
                  resources["ShortcutKeyBgBrush"] = new SolidColorBrush(ColorFromHex("#CABAA2"));
                  resources["ShortcutKeyShadowBrush"] = new SolidColorBrush(ColorFromHex("#A0917C"));
                  resources["BadgeBgBrush"] = new SolidColorBrush(ColorFromHex("#F3E6E6"));
                  resources["BadgeFgBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));
                  resources["StatusMutedBrush"] = new SolidColorBrush(ColorFromHex("#8A6012"));
                  resources["StatusSuccessBrush"] = new SolidColorBrush(ColorFromHex("#2E5A27"));
                  resources["StatusErrorBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));

                  resources["AccentBlueBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));
                  resources["WarningRedBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));

                  resources["ConnectButtonBgBrush"] = new SolidColorBrush(ColorFromHex("#8A1212"));
                  resources["ConnectButtonHoverBgBrush"] = new SolidColorBrush(ColorFromHex("#A80505"));
                  resources["ConnectButtonBorderBrush"] = new SolidColorBrush(ColorFromHex("#3D1B1B"));
                  resources["ConnectButtonConnectedBgBrush"] = new SolidColorBrush(ColorFromHex("#433E3B"));
                  resources["ConnectButtonConnectedHoverBgBrush"] = new SolidColorBrush(ColorFromHex("#5C5551"));
                  resources["ConnectButtonConnectedFgBrush"] = new SolidColorBrush(ColorFromHex("#EADDC9"));
              }
              else
              {
                  var darkBg = new LinearGradientBrush();
                  darkBg.StartPoint = new Point(0, 0);
                  darkBg.EndPoint = new Point(0, 1);
                  darkBg.GradientStops.Add(new GradientStop(ColorFromHex("#161413"), 0.0));
                  darkBg.GradientStops.Add(new GradientStop(ColorFromHex("#0A0909"), 1.0));
                  resources["WindowBgBrush"] = darkBg;

                  resources["PanelBackgroundBrush"] = new SolidColorBrush(ColorFromHex("#1B1917"));
                  resources["ChannelCardBgBrush"] = new SolidColorBrush(ColorFromHex("#24201E"));
                  resources["ConsoleBorderBrush"] = new SolidColorBrush(ColorFromHex("#3B3530"));
                  resources["TextForegroundBrush"] = new SolidColorBrush(ColorFromHex("#EADDC9"));
                  resources["TextSecondaryBrush"] = new SolidColorBrush(ColorFromHex("#C2B59D"));
                  resources["TextMutedBrush"] = new SolidColorBrush(ColorFromHex("#807361"));
                  resources["NoDevicesTextBrush"] = new SolidColorBrush(ColorFromHex("#3D3732"));
                  resources["ListBoxSeparatorBrush"] = new SolidColorBrush(ColorFromHex("#2E2824"));
                  resources["ControlBgBrush"] = new SolidColorBrush(ColorFromHex("#0F0E0D"));
                  resources["ControlBorderBrush"] = new SolidColorBrush(ColorFromHex("#423C37"));
                  resources["ThumbBgBrush"] = new SolidColorBrush(ColorFromHex("#A80505"));
                  resources["ThumbBorderBrush"] = new SolidColorBrush(ColorFromHex("#C10B0B"));
                  resources["DelayTextBrush"] = new SolidColorBrush(ColorFromHex("#DCA462"));
                  resources["StatusPanelBgBrush"] = new SolidColorBrush(ColorFromHex("#141211"));
                  resources["ShortcutBgBrush"] = new SolidColorBrush(ColorFromHex("#1C1917"));
                  resources["ShortcutKeyBgBrush"] = new SolidColorBrush(ColorFromHex("#2C2723"));
                  resources["ShortcutKeyShadowBrush"] = new SolidColorBrush(ColorFromHex("#0A0909"));
                  resources["BadgeBgBrush"] = new SolidColorBrush(ColorFromHex("#2D0B0B"));
                  resources["BadgeFgBrush"] = new SolidColorBrush(ColorFromHex("#FF3E3E"));
                  resources["StatusMutedBrush"] = new SolidColorBrush(ColorFromHex("#C29F72"));
                  resources["StatusSuccessBrush"] = new SolidColorBrush(ColorFromHex("#859F6C"));
                  resources["StatusErrorBrush"] = new SolidColorBrush(ColorFromHex("#A80505"));

                  resources["AccentBlueBrush"] = new SolidColorBrush(ColorFromHex("#A80505"));
                  resources["WarningRedBrush"] = new SolidColorBrush(ColorFromHex("#C10B0B"));

                  resources["ConnectButtonBgBrush"] = new SolidColorBrush(ColorFromHex("#A80505"));
                  resources["ConnectButtonHoverBgBrush"] = new SolidColorBrush(ColorFromHex("#C10B0B"));
                  resources["ConnectButtonBorderBrush"] = new SolidColorBrush(ColorFromHex("#EADDC9"));
                  resources["ConnectButtonConnectedBgBrush"] = new SolidColorBrush(ColorFromHex("#2D2723"));
                  resources["ConnectButtonConnectedHoverBgBrush"] = new SolidColorBrush(ColorFromHex("#3D3530"));
                  resources["ConnectButtonConnectedFgBrush"] = new SolidColorBrush(ColorFromHex("#EADDC9"));
              }

              if (isLight)
              {
                  ThemeButtonText.Text = "CAMPFIRE MODE";
                  ThemeIconPath.Data = Geometry.Parse("M9,2C7.95,2 6.95,2.23 6.05,2.63C9,3.87 11,6.7 11,10C11,13.3 9,16.13 6.05,17.37C6.95,17.77 7.95,18 9,18A8,8 0 0,0 17,10A8,8 0 0,0 9,2Z");
                  ThemeButton.Foreground = new SolidColorBrush(ColorFromHex("#8A1212"));
              }
              else
              {
                  ThemeButtonText.Text = "FRONTIER MODE";
                  ThemeIconPath.Data = Geometry.Parse("M12,7A5,5 0 0,0 7,12A5,5 0 0,0 12,17A5,5 0 0,0 17,12A5,5 0 0,0 12,7M12,2A1,1 0 0,1 13,3V5A1,1 0 0,1 12,6A1,1 0 0,1 11,5V3A1,1 0 0,1 12,2M12,18A1,1 0 0,1 13,19V21A1,1 0 0,1 12,22A1,1 0 0,1 11,21V19A1,1 0 0,1 12,18M2,12A1,1 0 0,1 3,11H5A1,1 0 0,1 6,12A1,1 0 0,1 5,13H3A1,1 0 0,1 2,12M18,12A1,1 0 0,1 19,11H21A1,1 0 0,1 22,12A1,1 0 0,1 21,13H19A1,1 0 0,1 18,12M5.63,4.22A1,1 0 0,1 7.05,4.22L8.46,5.64A1,1 0 0,1 8.46,7.05A1,1 0 0,1 7.05,7.05L5.63,5.64A1,1 0 0,1 5.63,4.22M15.54,14.14A1,1 0 0,1 16.95,14.14L18.36,15.56A1,1 0 0,1 18.36,16.97A1,1 0 0,1 16.95,16.97L15.54,15.56A1,1 0 0,1 15.54,14.14M18.36,4.22A1,1 0 0,1 18.36,5.64L16.95,7.05A1,1 0 0,1 15.54,7.05A1,1 0 0,1 15.54,5.64L16.95,4.22A1,1 0 0,1 18.36,4.22M8.46,14.14A1,1 0 0,1 8.46,16.97L7.05,18.36A1,1 0 0,1 5.63,18.36A1,1 0 0,1 5.63,16.97L7.05,14.14A1,1 0 0,1 8.46,14.14Z");
                  ThemeButton.Foreground = (SolidColorBrush)FindResource("AccentBlueBrush");
              }
          }

          private Color ColorFromHex(string hex)
          {
              return (Color)ColorConverter.ConvertFromString(hex);
          }

          private void PresetButton_Click(object sender, RoutedEventArgs e)
          {
              var button = sender as System.Windows.Controls.Button;
              if (button == null) return;

              var item = button.DataContext as DeviceItem;
              if (item == null) return;

              string tag = button.Tag?.ToString() ?? "Reset";
              switch (tag)
              {
                  case "Campfire":
                      item.Bass = 4f;
                      item.Mid = -2f;
                      item.Treble = 2f;
                      break;
                  case "Gunslinger":
                      item.Bass = 6f;
                      item.Mid = 0f;
                      item.Treble = 4f;
                      break;
                  case "Saloon":
                      item.Bass = -2f;
                      item.Mid = 4f;
                      item.Treble = 5f;
                      break;
                  case "Reset":
                  default:
                      item.Bass = 0f;
                      item.Mid = 0f;
                      item.Treble = 0f;
                      break;
              }
          }

          protected override void OnClosed(EventArgs e)
          {
              viewModel.SaveProfile();
              viewModel.Disconnect();

              if (notifyIcon != null)
              {
                  notifyIcon.Visibility = Visibility.Collapsed;
                  notifyIcon.Dispose();
              }
              base.OnClosed(e);
          }
      }
  }
  ```

- [ ] **Step 3: Update bindings in MainWindow.xaml**
  Modify `C:\Users\sugum\source\repos\SoundSync\SoundSync\MainWindow.xaml` to bind controls to ViewModel:
  - Add standard BooleanToVisibilityConverter: `<BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter"/>` in Window.Resources.
  - Update `ListBox x:Name="DeviceListBox" ItemsSource="{Binding Devices}" ...`
  - Update `RefreshButton Command="{Binding RefreshCommand}" ...`
  - Update `ConnectButton Command="{Binding ConnectCommand}" Content="{Binding ConnectButtonText}" Tag="{Binding ConnectButtonTag}" ...`
  - Update `UpdateBanner Visibility="{Binding IsUpdateBannerVisible, Converter={StaticResource BooleanToVisibilityConverter}}" ...`
  - Update `UpdateText Text="{Binding UpdateText}" ...`
  - Update `UpdateDownloadButton Command="{Binding DownloadUpdateCommand}" ...`

- [ ] **Step 4: Run test to verify it passes**
  Run: `dotnet test`
  Expected: PASS.

- [ ] **Step 5: Commit changes**
  Run:
  ```bash
  git add SoundSync/MainWindow.xaml SoundSync/MainWindow.xaml.cs SoundSync.Tests/
  git commit -m "refactor: complete MVVM decoupling of MainWindow UI from business logic"
  ```
