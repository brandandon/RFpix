# Testing RFpix on the air

Thanks for helping test RFpix (now in beta), a HamDRM (digital SSTV) program for Windows by KD9DD.
RFpix has ten built-in **test transmissions**: fixed pictures sent with fixed settings,
and each picture says what its settings are. When you receive one, RFpix checks it against
its own copy of the original and writes down how it came through.

## Getting it

Download one of these (below, under *Assets*):

- **RFpix-Setup-x.y.z.exe** (recommended) installs RFpix. You choose the folder (no administrator
  rights needed), and it adds RFpix to the Start menu. Uninstall it from Windows' Settings > Apps
  like any other program. Running a newer installer updates RFpix in place.
- **RFpix.exe** runs without installing: one file, run it from anywhere. (It unpacks a copy of
  its parts into a hidden temporary folder the first time each version runs.)

Windows may say *"Windows protected your PC"* the first time, because RFpix is new and not yet
signed. Click **More info**, then **Run anyway**.

Your settings live in %AppData%\RFpix, and your received pictures, recordings and test results
in Documents\RFpix. Uninstalling asks whether to remove the settings, and never removes your files.

## Receiving tests (the most useful help)

1. At the bottom of the **Settings** page's **Radio** tab, pick your radio's sound input (usually *Microphone (USB Audio CODEC)*)
   and output (usually *Speakers (USB Audio CODEC)*).
2. On the **TX/RX** page's **Receive** tab, click **Start listening**. Tune to the agreed
   frequency (USB on HF).
3. That's all. When a test transmission ends, RFpix shows a line such as
   **"Test 2 from KD9DD: perfect · quality 24 dB (needs about 11)"**,
   or how many of the picture's pieces arrived if it didn't come through. Each sending is judged
   on its own: if a test is sent twice, the second isn't helped by pieces from the first.
4. Every test received is added to **Documents\RFpix\Test results.csv** (button:
   **Open test results**). It opens in Excel. **Please email that file back** to the sender.
5. If something odd happens, click **Record** before the next test: the recording
   (in Documents\RFpix\Recordings) shows exactly what your radio heard.

## Sending tests

1. **Settings** page, **Radio** tab: pick your radio, its COM port and how it's keyed (CAT, RTS,
   DTR or VOX), then **Connect** and **Test PTT**. (Use a dummy load or low power for the PTT test.)
   On the **Misc** tab, type your callsign: it's sent with every transmission.
2. **Beta Testing** page: pick a test and click **Send**. (The sound output is the one picked
   on the Settings page's Radio tab, at the bottom.) RFpix goes on listening on the TX/RX page's
   Receive tab while you send.
3. **Level:** leave the radio's Windows volume where your other programs need it and set the
   level with RFpix's slider. Start around **−30 dB** and raise it until the radio's ALC
   barely moves. The slider works while sending. Too much level is the most common cause
   of failed pictures: the settings still decode but the picture doesn't.

| Test | Settings | Tests |
| --- | --- | --- |
| 1 | Mode B · 2.5 kHz · 4-QAM · long · high | the toughest: try this first |
| 2 | Mode B · 2.5 kHz · 16-QAM · long · high | the usual net setting |
| 3 | Mode B · 2.5 kHz · 16-QAM · long · low | less error correction, a little faster |
| 4 | Mode B · 2.5 kHz · 64-QAM · long · high | fastest, needs a very clean signal |
| 5 | Mode A · 2.5 kHz · 16-QAM · long · high | mode A: for steady paths |
| 6 | Mode E · 2.5 kHz · 16-QAM · long · high | mode E: for fast-changing paths |
| 7 | Mode B · 2.2 kHz · 16-QAM · long · high | the narrow 2.2 kHz width |
| 8 | Mode B · 2.5 kHz · 16-QAM · short · high | short interleave: less protection against fading |
| 9 | Mode B · 2.5 kHz · 4-QAM · long · high · RS3 | test 1 with Reed-Solomon, for weak paths: rebuilds up to a third missing |
| 10 | Mode B · 2.5 kHz · 16-QAM · long · high · RS2 | test 2 with Reed-Solomon: rebuilds up to a quarter missing |

## Sending your own pictures (new in v0.3.3)

1. **TX/RX** page, **Send** tab: click **Open picture…** or drag a picture onto the page.
   Phone photos (including iPhone HEIC) are fine.
2. RFpix fits it to the **transmit time** (2:00 unless changed) as soon as it opens, making the
   picture smaller and compressing it; it fits it again when you change the format, settings or
   time. The **Size** and **Quality** sliders fine-tune the result. The box on the right shows
   how long it will take: green fits the target (the automatic fit may use 3 seconds extra),
   yellow is up to 7 seconds over, red is more.
3. **Original** and **As sent** are side by side. Tick **Zoom to full size** to check small text,
   and drag either picture to look around.
4. **Format:** JPEG 2000 (the default) and JPEG are shown by QSSTV, EasyPal and RFpix.
   **AVIF** looks sharper for its size, so pictures go out sharper or faster, but **only RFpix
   shows it**: use it only when everyone receiving has RFpix.
5. The settings (mode, bandwidth, QAM, interleave, protection, Reed-Solomon) are drop-downs.
   **Net repeater (70 cm)** is built in; **Save as profile…** keeps your own.
6. The level and **Send** are under the As sent picture and work as for the tests. Please report how the pictures arrived,
   and anything on the Send tab that's confusing or doesn't work.

## Beta: Ham Radio Deluxe, and analog pictures without VIS (new in v0.5.2 beta)

- **Settings > Radio**: pick **Shared through Ham Radio Deluxe** when HRD's Rig Control has the
  radio. Start HRD and connect it to the radio first, then click **Connect** in RFpix (the address is
  usually localhost:7809). RFpix keys the radio with HRD's TX button (or use VOX), shows the frequency
  and mode, and can switch data mode while transmitting (tick the box; on an IC-9700 it's HRD's Data
  button). Please try **Test PTT**, and say what the status line reports about data mode.
- **Receive** tab: an analog picture whose VIS code was missed (you started listening partway
  through, or the code was lost in noise) is now found from its sync pulses, after about five lines.
  It starts at the top of the frame with the line it was found on, and is saved and uploaded like
  the others, marked "joined partway". Please try starting to listen in the middle of a picture.
- **Settings > Misc**: the callsign box now sits under its label.
- The **Alpha Testing** page is now **Beta Testing**.

## Screens that fit the window (new in v0.5.1)

- Every page (and the pop-out Receive window) now **shrinks to fit** when the window is too small
  for it, so the buttons stay in view without scrolling. It never grows bigger than normal, and
  never shrinks below 60%; in a window smaller than that, the page scrolls. Please try making the
  window small and large, on each page, and say where anything looks wrong or gets cut off.
- **Send** tab: in a short window the two pictures get smaller first, so everything else keeps its
  normal size for longer.
- **Receive** tab: the signal and settings boxes now show only while a picture is arriving; their
  space stays, empty, the rest of the time. In a narrow window the boxes wrap onto more lines.
- **Send** tab, time on the air: the note about waterfall text no longer says where it's set.

## Analog like MMSSTV, and the other changes in v0.5.0

- **Analog SSTV sending**: pictures now go out as MMSSTV sends them: a short preamble (eight quick
  tones, 0.8 second) before the VIS code, and your callsign as an **FSK ID** (a short warble) after
  the picture. MMSSTV and QSSTV show the FSK ID as who sent it: please check they do.
  Under the analog mode, **Send my callsign in CW after the picture** adds your callsign in Morse
  code half a second later (the same tone and speed as the CW button).
- **Fill the frame / Whole picture**: the picture as sent now changes both ways.
- **Previews**: the two pictures on the Send tab are 10% smaller.
- **Receive** tab: while listening, the digital boxes always show (Offset, Clock, Strength,
  Quality, Mode, Bandwidth, QAM, Interleave, Protection, Reed-Solomon, From), empty unless a
  digital picture is arriving. While an analog picture arrives they're replaced by analog boxes:
  Mode, Line ___ of ___, and From (the sender's FSK ID). They stay 10 seconds after the picture
  ends, then go back to the digital boxes. A picture whose FSK ID is read gets that callsign in the
  Gallery.
- **Waterfall**: with the Send tab set to Analog (or while an analog picture arrives), the marks
  change to SSTV's tones: S 1200 Hz (sync), B 1500 Hz (black), V 1900 Hz (VIS leader) and
  W 2300 Hz (white). Point at a mark to see what it is.
- **Gallery**: the Received tab shows Digital pictures on the left and Analog on the right; the
  Sent tab says Analog or Digital beside each picture's time.
- **Settings > FTP server**: **Pictures shown Digital** and **Pictures shown Analog** set how many
  of each the website keeps (for example 3 digital and 12 analog). Click **Save**.
- **Settings > Radio**: the sound devices (receive input and send output) are now at the bottom
  of this tab; the Audio tab is gone.
- **Settings > Misc**: **Your grid square (QTH)**, 4 or 6 characters. Templates can show it with
  the **%qth** code.

## Digital and analog kept apart, signal boxes (new in v0.4.5)

- **Received pictures**: HamDRM pictures now go to `Received\Digital` and analog SSTV pictures to
  `Received\Analog`. Pictures already in `Received` stay where they are; the Gallery's Received
  tab shows all three folders together.
- **Website copies**: copies go to the Digital and Analog subfolders of the copies folder (for
  example `C:\FTPIMAGE\Digital`). **Settings > FTP server** has two server folders, one for each;
  each kind gets its own image1 to image12 on the website. **Test** checks both server folders.
  Click **Save** after changing anything.
- **Receive** tab: the Offset, Clock, Strength and Quality boxes are always there, each a fixed
  size, and empty while there's no HamDRM signal ("No HamDRM signal" is gone). Quality shows the
  dB needed in brackets, for example "14 dB (11)".

## Overlay templates (new in v0.4.2 to v0.4.4)

- **Send** tab, **Template…** (under Open picture): pick a template from the thumbnails (each is
  shown on your picture), or None. It's drawn on the picture as it goes out, in Digital and Analog
  alike, at the size that's sent, so the text stays sharp on a small analog frame and a large
  digital picture. The "As sent" box shows the result.
- Templates can hold codes filled in when sending: **%me** (your callsign), **%to** and **%name**
  (the station you're sending to), **%rst**, **%date**, **%time** and **%utc**. The To, Name and RST
  boxes appear beside the Template button when the template uses them.
- RFpix comes with five: Callsign bar, CQ SSTV net, Reply, Framed and Date stamp.
- **New template…**, **Edit…** (or **Copy and edit…** for RFpix's own) open the editor: click an item
  on the picture to choose it, drag it to move it, drag its corner square to resize it. Add text,
  boxes (plain, see-through or fading between two colours, with rounded corners), logos (a PNG with a
  see-through background works best) and a border round the whole picture. **Preview** switches
  between an analog (320 × 256) and a digital (1280 × 960) picture, to check it works at both sizes.
- Your templates are files in Documents\RFpix\Templates (logos included): **Open templates folder**
  in the picker, to back them up or share them with the net.

## Analog SSTV (new in v0.4.0)

- **Send** tab: **Send as** at the top: **Digital (HamDRM)** as before, or **Analog SSTV**. In analog,
  pick the mode on the right (Martin 1 and 2, Scottie 1, 2 and DX, Robot 36 and 72, PD 50 to PD 290;
  each shows its size and time) and how the picture fits the mode's frame: **Fill the frame** (the
  edges that don't fit are cut off) or **Whole picture** (black bars). The repeater time-out and
  lead-in count as for HamDRM. Analog pictures go out without waterfall text, so ID, WF TXT and
  WF PIC are hidden in analog; they come back, with their settings, in Digital.
- **Receive** tab: nothing to set. RFpix listens for analog SSTV and HamDRM at the same time; an
  analog picture appears line by line, then is saved (as PNG), shown in the Gallery and copied for
  the website. A picture is recognised by its start code (VIS), so tune in before it begins.
- Please try sending to and receiving from MMSSTV and QSSTV stations in the modes the net uses, and
  tell us how the pictures look (slanted, wrong colours, cut short).

## Website copies and FTP upload (new in v0.4.0)

**Settings > FTP server**:
- **Save a JPEG copy of each picture received** puts a copy in a folder (C:\FTPIMAGE to start with),
  with the date and time in the lower left corner, as MMSSTV does. Tick **Copy the pictures I send
  too** for your own. The pictures in the Received folder stay as they came.
- **Upload new pictures from that folder to my website** works like the SSTV Auto Uploader: the
  newest picture becomes image1.jpg and the others move up, so the site shows the latest 12; each
  file then moves into the folder's Archives subfolder. Fill in the server (FTP, FTPS or SFTP),
  user name and password, click **Test the connection**, then **Save**. Don't run the SSTV Auto
  Uploader on the same folder at the same time (or leave RFpix's upload off and keep using it).
- The upload log at the bottom shows each upload, and any problem (RFpix tries again every 30 seconds).

## Waterfall text and pictures, pop-out Receive (new in v0.3.7)

- **Send** tab, next to **ID**: **WF TXT** opens a small window to type up to 40 characters to write
  on everyone's waterfall, with a preview and how long it takes. **WF PIC** draws a small picture on
  the waterfall in shades of grey: choose a picture (a logo or simple drawing works best); it's
  fitted into a square 108 pixels across (250 to 2780 Hz, up to about 14 seconds). Tick **Invert**
  if a dark logo looks better reversed, and check the preview.
- **TX/RX**: **Pop out Receive** (right of the tabs) moves the Receive tab into its own window, so
  you can watch it while you use the Send tab. Listening carries on. Close that window (or click
  **Bring Receive back**) to put it back. RFpix remembers where it was, and whether it was out.
- **Receive** tab: the signal's offset, clock, strength and quality are in boxes; **Quality** is green
  when it's good enough for the settings being sent, red when it isn't.

## Missing pieces (BSR) (new in v0.3.6)

When a picture arrives with pieces missing, a **Missing pieces** box appears on the **Receive** tab,
under the picture. Pick the picture (the latest is first) and click **Ask for them (BSR)**: RFpix
sends a short request (about 15 seconds) in the settings the picture came with. The sender's RFpix,
EasyPal or QSSTV can then send just those pieces, and the picture completes and is saved. Keep
listening after asking.

When someone asks you for pieces of one of your last 6 pictures, a yellow bar appears at the top of
**TX/RX** (on either tab) saying who asked and how long the answer takes. Nothing is sent until you
click **Send the pieces**; **Not now** closes it. A request for someone else's picture shows a short
grey note. RFpix must be listening (Receive tab started) to hear requests. Please tell us how it
goes with EasyPal and QSSTV stations: asking them, and answering them.

## Gallery (new in v0.3.6)

The **Gallery** page shows every picture received and sent, newest first, grouped by day. Click one
for its details (who sent it, when, the settings it came with, the signal); double-click to open it
full size. **Send this picture** opens it on the Send tab; **Delete** moves it to the Recycle Bin
(click twice). Sent pictures are now saved, as they went out, in Documents\RFpix\Sent. Pictures
received before v0.3.6 show too, without the details.

## Repeater time-out and lead-in (new in v0.3.5)

On the **Send** tab, beside the other settings: **Repeater time-out** is the longest your repeater
stays on the air for one transmission (often 3:00; None for simplex or HF). RFpix keeps the whole
transmission (lead-in, waterfall text and picture) inside it: the transmit time can't be set longer,
the picture is fitted shorter if need be, and anything that would run over isn't sent.
**Lead-in** is the quiet after keying before the picture starts, while the repeater opens: raise it
if the start of your pictures gets lost. Both are saved with a profile; "Net repeater (70 cm)" has
a 3:00 time-out and 0.3 s lead-in.

The **Receive** tab now shows each received transmission's settings (mode, bandwidth, QAM, interleave,
protection, Reed-Solomon, callsign) in boxes, like the Send tab's.

## Sound-card calibration (new in v0.3.5)

**Settings > Misc**, **Sound-card calibration**: click **Measure (2 minutes)** with the radio on and
receiving. RFpix listens to the radio's sound input and plays silence to its output (the radio isn't
keyed), counting both against the computer's clock, and then corrects for any difference when
receiving and sending. Make sure Windows' clock is set right first (Settings > Time & language >
Date & time > Sync now). Please send the numbers it shows: they tell us how far off typical sound
cards are.

## IC-9700 data mode (new in v0.3.5)

**Settings > Radio**, with the IC-9700 picked: tick **Switch the IC-9700 to data mode while
transmitting**. RFpix turns the radio's DATA setting on just before keying (FM → FM-D, USB → USB-D)
and off afterwards, keeping the mode and filter; the status line shows it while sending, and
**Test PTT** reports it. The box takes effect at once, connected or not.

## Waterfall text, CW and ID (new in v0.3.4)

- **Settings > Misc**, **Waterfall text**: type a short message to write on everyone's waterfall
  before and/or after each of your pictures (for example "CQ SSTV NET" and "73 de KD9DD").
  Leave a box empty for none.
- **Send** tab, under the format box: **CW** sends your callsign in Morse code (750 Hz,
  15 words per minute); **ID** writes your callsign on the waterfall. Both use your callsign
  from Settings > Misc, and the level slider.

QSSTV and EasyPal users can receive the tests too (they're ordinary HamDRM pictures);
just tell the sender which ones arrived.

73, Dan KD9DD
