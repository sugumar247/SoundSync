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


<img width="996" height="859" alt="SoundSync Red dead Edition(dark)" src="https://github.com/user-attachments/assets/ddebfa3e-aaeb-4e9c-a719-ff08a172a780" />
<img width="991" height="855" alt="SoundSync Red dead Edition(light)" src="https://github.com/user-attachments/assets/e76ec91a-c8cd-4c0c-b2b5-80a3b0294b08" />

<img width="1920" height="894" alt="Screenshot (19)" src="https://github.com/user-attachments/assets/a9297c49-3057-4618-b716-a896aa24257f" />



## ✨ Features

- **Multi-Endpoint Routing:** Play a single audio source (like a YouTube video or Spotify or any audio source) through multiple headphones or speakers at the exact same time.
- **Aggressive Latency Control:** Implements a custom ring-buffer clearing strategy to combat hardware clock drift, ensuring secondary devices stay within ~10-30ms of real-time.
- **Anti-Feedback Loop Protection:** Automatically detects your default Windows playback device and prevents audio from being routed back into it, completely eliminating infinite echo loops.
- **Dynamic Device Scanning:** Easily refresh the device list to detect newly plugged-in USB or Bluetooth headphones without restarting the app.
- **Modern UI:** Built with Windows Presentation Foundation (WPF) featuring a sleek, dark-mode graphical interface.

## ⚙️ How it Works

The application uses `WasapiLoopbackCapture` to intercept the raw audio bytes flowing to your default Windows audio endpoint. 

When the user clicks "Connect", the app initializes a separate `WasapiOut` stream for every selected hardware device in Shared Mode. As Windows fires `DataAvailable` events containing the live audio bytes, the application aggressively broadcasts those bytes into a `BufferedWaveProvider` attached to every selected output.

## 🚀 Getting Started

### 📥 Download & Installation (For Regular Users)

You do not need to install anything to use SoundSync! 

1. Go to the [Releases Page](https://github.com/sugumar247/SoundSync/releases).
2. Download Latest **SoundSync.exe**.
3. Double-click the file to run it. That's it!
4. If you face any issue, open the application with  "Run as Administrator".

### 🎛️ How to Use It (The Basics)

SoundSync works by capturing audio from your **Default Windows Output** and mirroring it to other devices.

1. **Do nothing with your main speakers** — whatever is set as your default Windows audio device is automatically your **Source**.
2. Open SoundSync and **check the boxes** for the devices you want to mirror audio to (e.g., your secondary headphones or TV). 
3. *Note: Do NOT check the box for your default device. It is already the source.*
4. Click **Connect**. Audio will now play through your main speakers AND all selected devices!

### 💻 Building from Source (For Developers)

1. Clone the repository:
   ```bash
   git clone https://github.com/sugumar247/SoundSync.git
   ```
2. Open `SoundSync.slnx` in Visual Studio 2022.
3. The project relies on the [NAudio](https://github.com/naudio/NAudio) library. Visual Studio should restore this NuGet package automatically. If not, run:
   ```bash
   dotnet restore
   ```
4. Press `F5` to compile and run the application.

## 🎧 Usage & "Perfect Sync" Tutorial

If you are trying to share a movie with a friend using two pairs of headphones, you will notice a slight ~30ms delay on the 2nd pair of headphones if you capture audio directly from the 1st pair. 

To achieve **100.00% perfect synchronization** with zero echoes, use the "Dummy Hardware" trick:

1. Look at your Windows sound settings and find an audio device you aren't currently using (e.g., an HDMI Monitor with no speakers, or a Virtual Audio Cable like *VB-Audio Cable*).
2. Set that silent device as your **Default Windows Output**. (Your computer will go silent).
3. Open **SoundSync**.
4. Check the boxes for **Headphone 1** and **Headphone 2** or more. 
5. Click **Connect**. 

The app will now capture the silent audio stream, duplicate it, and push it to *both* sets of headphones via the C# engine. Because they share the exact same routing path, their latency is identical and they are perfectly synchronized!

## 🛠️ Built With

* **C# / .NET 10.0** - The core framework
* **WPF** - UI Framework
* **[NAudio](https://github.com/naudio/NAudio)** - Audio and WASAPI interaction

## 🤝 Contributing

Contributions, issues, and feature requests are welcome! 

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## 📝 License

Distributed under the MIT License. See `LICENSE` for more information.
