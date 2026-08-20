<table align="center" border="0" cellpadding="0" cellspacing="0">
  <tr>
    <td valign="middle">
      <img src="assets/logo.png" width="60" height="60" alt="SoundSync Logo" />
    </td>
    <td valign="middle" style="padding-left: 15px;">
      <h1>SoundSync</h1>
    </td>
  </tr>
</table>

<p align="center">
 <img src="https://img.shields.io/github/v/release/sugumar247/SoundSync?style=for-the-badge&color=2EA44F" alt="Latest Release" />
  <img src="https://img.shields.io/badge/PLATFORM-WINDOWS-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Platform" />
  <img src="https://img.shields.io/badge/LANGUAGE-C%23%20%7C%20.NET%2010-239120?style=for-the-badge&logo=dotnet&logoColor=white" alt="Language" />
  <img src="https://img.shields.io/badge/UI-WPF-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt="Framework" />
  <img src="https://img.shields.io/badge/LICENSE-MIT-b31b1b?style=for-the-badge" alt="License" />
  <img src="https://img.shields.io/github/release-date/sugumar247/SoundSync?style=for-the-badge&color=007ACC" alt="Release Date" />
  <!-- Counts ALL assets across ALL releases historically -->
<img src="https://img.shields.io/github/downloads/sugumar247/SoundSync/total?style=for-the-badge&color=E65100" alt="Downloads" />
  <img src="https://img.shields.io/github/repo-size/sugumar247/SoundSync?style=for-the-badge&color=424242" alt="Repo Size" />
  <img src="https://img.shields.io/github/issues/sugumar247/SoundSync?style=for-the-badge&color=D32F2F" alt="Open Issues" />

</p>




<p align="center">
  A lightweight, low-latency C# WPF application that captures your system's audio and routes it to multiple hardware output devices simultaneously. Built entirely on the Windows Core Audio API (WASAPI) using the NAudio library.
</p>

<img width="1920" height="1011" alt="Screenshot (69)" src="https://github.com/user-attachments/assets/f6f0d33f-e3ae-4860-8a59-05aa843ec048" />
<img width="1920" height="1008" alt="Screenshot (68)" src="https://github.com/user-attachments/assets/c0d65f88-4f3c-420b-8baa-0c77dd495e9a" />

<img width="1920" height="894" alt="Screenshot (19)" src="https://github.com/user-attachments/assets/a9297c49-3057-4618-b716-a896aa24257f" />


 ## ✨ Features
  • Multi-Endpoint Routing: Play a single audio source (like a YouTube video, Spotify, or games) through multiple
  headphones or speakers at the exact same time.
  
  • Network Audio Streaming (New!): Stream your PC audio to any smartphone browser on your local network. You can
  also connect desktop media players like VLC or foobar2000 using dedicated raw streams. Secured with token
  authentication.
  
  • Resilient Audio Engine: Safely hot-swap headphones, dock monitors, or connect via Remote Desktop without
  crashing. The audio session automatically detects changes and gracefully rebuilds itself in the background.
  
  • Intelligent Format Conversion: Seamlessly mirror audio between devices with completely different formats. Mix
  and match 4-channel surround outputs and stereo headphones—SoundSync handles the channel mapping and resampling
  automatically.
  
  • Per-Device Delay & Volume: Precisely align audio with absolute millisecond delay sliders per output. Every
  connected device (local or remote) gets its own true volume control, completely independent of the master Windows
  volume.
  
  • Anti-Feedback Loop Protection: Automatically detects your default Windows playback device and prevents audio
  from being routed back into it, eliminating infinite echo loops. 
  
  • Modern UI: Built with Windows Presentation Foundation (WPF) featuring a sleek, dark-mode graphical interface,
  native system fonts, and clear error explanations.

  ## ⚙️ How it Works

  The application uses WasapiLoopbackCapture to intercept the raw audio bytes flowing to your default Windows audio
  endpoint.
  When you connect, the signal path goes through three layers:
  1. Compatibility: Channels are mapped (e.g., folding 4-channel audio into stereo) and resampled to match each
  target device.
  2. Distribution: A clean copy of the audio is created. The master Windows volume is mathematically divided back
  out, ensuring all mirrored outputs receive a pure signal.
  3. Adjustments: Per-consumer volume, equalizers, and millisecond delays are applied right before the audio hits
  the secondary hardware or network socket.
  ## 🚀 Getting Started
  ### 📥 Download & Installation (For Regular Users)

  You do not need to install anything to use SoundSync!

  1. Go to the Releases Page https://github.com/sugumar247/SoundSync/releases.
  2. Download Latest SoundSync.exe.
  3. Double-click the file to run it. That's it!
  4. (If you face any issues saving settings or setting default devices, open the application with "Run as
  Administrator").

  ### 🎛️ How to Use It (Local Devices)

  SoundSync works by capturing audio from your Default Windows Output and mirroring it to other devices.
  1. Do nothing with your main speakers — whatever is set as your default Windows audio device is automatically your
  Source.
  2. Open SoundSync and check the boxes for the devices you want to mirror audio to (e.g., your secondary headphones
  or TV).
  3. Note: Do NOT check the box for your default device. It is already the source.
  4. Click Connect. Audio will now play through your main speakers AND all selected devices!
  ### 📱 How to Use It (Network Streaming)

  1. Click Connect in SoundSync.
  2. The app will generate a secure Web URL and a token.
  3. Open that URL on your phone or tablet's browser on the same Wi-Fi network.
  4. Tap to begin listening. You will see your device appear in the SoundSync desktop app, where you can control its
  volume independently!
  ### 💻 Building from Source (For Developers)

  1. Clone the repository:
    git clone https://github.com/sugumar247/SoundSync.git
    
  2. Open SoundSync.slnx in Visual Studio 2022.
  3. The project relies on the NAudio https://github.com/naudio/NAudio library. Visual Studio should restore this
  NuGet package automatically. If not, run:
    dotnet restore
    
  4. Press F5 to compile and run the application.

  ## 🎧 Usage & "Perfect Sync" Tutorial

  If you are trying to share a movie with a friend using two pairs of headphones, you might notice a slight delay on
  the 2nd pair of headphones if you capture audio directly from the 1st pair. You can now fix this in two ways:
  Because SoundSync now features absolute millisecond delay sliders per output, simply adjust the delay slider on
  the faster device until it perfectly matches the slower device.

  Method 2: The "Dummy Hardware" Trick (For 100% mathematical sync)

  1. Look at your Windows sound settings and find an audio device you aren't currently using (e.g., an HDMI Monitor
  Method 1: The Delay Sliders
  with no speakers, or a Virtual Audio Cable like VB-Audio Cable).
  2. Set that silent device as your Default Windows Output. (Your computer will go silent).
  3. Open SoundSync.
  4. Check the boxes for Headphone 1 and Headphone 2.
  5. Click Connect.

  Because both headphones are now receiving audio via the SoundSync distribution engine rather than one receiving it
  directly from Windows, their latency is identical.

  ## 🛠️ Built With

  • C# / .NET 10.0 - The core framework
  • WPF - UI Framework
  • NAudio https://github.com/naudio/NAudio - Audio and WASAPI interaction

  ## 🤝 Contributing

[View the full list of contributors who helped build SoundSync!](https://github.
  com/sugumar247/SoundSync/graphs/contributors)
  Contributions, issues, and feature requests are welcome!

  1. Fork the Project
  2. Create your Feature Branch (git checkout -b feature/AmazingFeature)
  3. Commit your Changes (git commit -m 'Add some AmazingFeature')
  4. Push to the Branch (git push origin feature/AmazingFeature)
  5. Open a Pull Request

  ## 📝 License

  Distributed under the MIT License. See LICENSE for more information.
