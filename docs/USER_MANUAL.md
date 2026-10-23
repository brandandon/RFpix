# RFpix User Manual

Version 0.5.0 · Oct 21, 2026 · KD9DD

This manual covers RFpix version 0.5.0 for Windows 10 and 11. If you're new to RFpix, read Part 1 and follow the setup checklist in section 3; it takes about 20 minutes and ends with your first picture received and sent.

The manual has three parts:

- **Part 1, Getting started:** what RFpix is, installing it, and setting it up.
- **Part 2, Using it:** receiving, sending digital and analog pictures, templates, the Gallery, and putting pictures on a website.
- **Part 3, Reference:** every setting, tab by tab; what RFpix's messages mean; and a glossary.

Words in **bold** are what you see on the screen: a page, tab, button or box. *\[Screenshot: …\]* marks where a picture of the screen will go.

## Part 1: Getting started

### 1. What RFpix is

RFpix sends and receives pictures by radio in two ways: **digital** (HamDRM, the same as EasyPal and QSSTV's digital mode) and **analog SSTV** (the same as MMSSTV and QSSTV's analog modes). It listens for both at once, so you don't have to say which is coming.

|  | Digital (HamDRM) | Analog SSTV |
| --- | --- | --- |
| Talks to | EasyPal, QSSTV (DRM), other RFpix stations | MMSSTV, QSSTV, most SSTV programs and apps |
| Sounds like | A steady hiss about 2.5 kHz wide | One warbling tone |
| The picture | Any size; arrives as an exact copy of the file sent | A fixed size for each mode; painted line by line |
| When the signal is weak | Pieces go missing; RFpix can ask for just those pieces again | Noise shows as speckles and streaks |
| Time on the air | You choose (RFpix fits the picture to it) | Set by the mode: 36 seconds to almost 5 minutes |

RFpix has four pages, chosen from the list on the left:

- **TX/RX:** the **Receive** tab (the waterfall, the signal, and the last picture received) and the **Send** tab (your picture, how it will go out, and the **Send** button).
- **Gallery:** every picture you've received and sent.
- **Settings:** your radio, sound devices, callsign, and website upload.
- **Beta Testing:** fixed test transmissions, for checking how well pictures get through.

A few things RFpix does for you:

- **Fits your picture to the time you choose.** Pick 1 minute, say, and RFpix works out the size and quality that just fit, and keeps you inside your repeater's time-out.
- **Repairs pictures.** When a digital picture arrives with pieces missing, one click asks the sender for just those pieces.
- **Saves everything.** Every picture received is saved on its own, and can be put on your website automatically.
- **Adds your callsign.** Templates draw your callsign, a CQ card or a reply on the picture as it goes out.

![The Receive tab (popped out into its own window), with a digital picture just received](images/receive-tab.png)

*The Receive tab, popped out into its own window: the waterfall on the left, the signal and settings boxes at the top, and the last picture received.*

### 2. Installing

RFpix is free to download from its [releases page on GitHub](https://github.com/brandandon/RFpix/releases/latest). It needs a 64-bit Windows 10 or 11 computer. Each release offers two files under **Assets**:

| File | What it is | Choose it if |
| --- | --- | --- |
| **RFpix-Setup-0.5.0.exe** | An installer | You want RFpix in the Start menu and easy to update (recommended) |
| **RFpix.exe** | The program in one file, nothing to install | You want to run it from a folder or a USB stick |

#### Installing with the installer

1. Download **RFpix-Setup-0.5.0.exe** and open it.
2. If Windows says *"Windows protected your PC"*, click **More info**, then **Run anyway**. Windows says this about new programs that aren't yet signed; it doesn't mean anything is wrong.
3. Accept the licence, and choose where to put RFpix. The suggested folder needs no administrator password.
4. Tick **Put a shortcut on the desktop** if you want one.
5. Click **Install**, then **Finish**. RFpix starts.

![Windows' "Windows protected your PC" message, with the More info link](images/smartscreen.png)

*Click **More info**: a **Run anyway** button appears beside **Don't run**.*

#### Running RFpix.exe without installing

Put **RFpix.exe** in any folder and double-click it. The first time each new version runs, it takes a few seconds longer while it unpacks its parts into a hidden folder. The *"Windows protected your PC"* message may appear here too; click **More info**, then **Run anyway**.

#### Updating

Download the newer installer and run it. It closes RFpix if it's running and replaces the old version. Your settings and pictures stay as they are.

#### Uninstalling

In Windows, open **Settings > Apps**, find **RFpix**, and choose **Uninstall**. RFpix asks whether to remove your settings too (radio, sound devices, callsign). Your pictures, recordings and test results in **Documents\RFpix** are always kept.

#### Where RFpix keeps things

| What | Where |
| --- | --- |
| Pictures received | Documents\RFpix\Received (in its Digital and Analog folders) |
| Pictures sent | Documents\RFpix\Sent |
| Recordings | Documents\RFpix\Recordings |
| Templates | Documents\RFpix\Templates |
| Test results | Documents\RFpix\Test results.csv |
| Settings | %AppData%\RFpix\settings.json |

### 3. First-time setup checklist

Work through these steps in order the first time you run RFpix. Everything is saved as you go, so you only do this once. You'll need the radio switched on and connected to the computer by its USB cable.

- [ ] **1. Enter your callsign and grid square.** Open **Settings**, then the **Misc** tab. Type your callsign in **Your callsign** (up to 9 characters; it goes out with every picture). Type your grid square in **Your grid square (QTH)**, 4 or 6 characters such as AA00 or AA00aa.
- [ ] **2. Pick your radio.** On the **Radio** tab, choose your radio from the **Radio** list. RFpix fills in the usual settings for it; the note under the list says what to check.
  - **Yaesu FT-991A:** choose the *Enhanced COM Port*. The baud rate must match the radio's menu 031 CAT RATE.
  - **Icom IC-9700:** choose the first of its two COM ports. Leave the CI-V address at A2 unless you've changed it in the radio.
  - **Another radio:** choose **Other radio**, then type part of its model (such as 7300) and pick it from the list.
  - **Another program already controls the radio:** choose **Shared through rigctld** or **Shared through flrig**.
  - **No cable for control:** choose **No radio control (VOX only)**, and turn VOX on in the radio.
- [ ] **3. Choose how the transmitter is keyed.** Under **Transmit keying (PTT)**, pick **CAT command**, **RTS line**, **DTR line** or **VOX**. CAT suits most radios; the IC-9700 works well with RTS. With an IC-9700, you can also tick **Switch the IC-9700 to data mode while transmitting**.
- [ ] **4. Connect and test the keying.** Click **Connect**: the radio's frequency appears. Then click **Test PTT (2 seconds)**. The radio transmits for 2 seconds with no audio, so use a dummy load, or low power on a clear frequency.
- [ ] **5. Pick the sound devices.** At the bottom of the **Radio** tab, under **Sound devices**, choose the radio's sound input and output. Radios usually show as *USB Audio CODEC*. Don't pick the computer's own speakers or microphone. Tip: rename the radio's devices in Windows' Sound settings (to *FT-991A*, say) to tell them apart.
- [ ] **6. Calibrate the sound card.** Back on the **Misc** tab, first check Windows' clock is right (Windows **Settings > Time & language > Date & time > Sync now**). Then click **Measure (2 minutes)** under **Sound-card calibration**. The radio stays receiving; RFpix doesn't key it. This corrects for a sound card that runs a little fast or slow, which would otherwise slant analog pictures and upset digital ones.
- [ ] **7. Receive something.** Open **TX/RX**, then the **Receive** tab, and click **Start listening**. The level bar should move with the band noise and stay out of orange; if it turns orange, turn down the radio's USB audio output. Tune to a frequency where pictures are being sent and wait: pictures appear on the right and are saved automatically.
- [ ] **8. Send your first picture.** Open the **Send** tab and click **Open picture…** (or drag a picture onto the tab). Choose **Digital (HamDRM)** or **Analog SSTV**, and set **Transmit time** or the analog mode. Then click **Send**.
- [ ] **9. Set the transmit level.** While sending, watch the radio's ALC meter and move the **Transmit level** slider until the ALC barely moves. The slider works during a transmission, and RFpix remembers where you leave it.

![Settings, Radio tab, set up for a Yaesu FT-991A, with the sound devices at the bottom](images/settings-radio.png)

*Settings, Radio tab, set up for a Yaesu FT-991A: the Enhanced COM Port at 38400 baud, CAT keying, and the radio's USB sound devices at the bottom.*

When all nine boxes are ticked, RFpix is ready. Part 2 explains receiving and sending in detail.

## Still to come

These sections will be written once Part 1 has been checked.

**Part 2: Using it**

4. Receiving
5. Sending digital (HamDRM)
6. Sending analog SSTV
7. Overlay templates
8. The Gallery
9. Putting pictures on your website
10. The Beta Testing page

**Part 3: Reference**

11. Settings, tab by tab
12. Troubleshooting
13. Glossary and file locations
