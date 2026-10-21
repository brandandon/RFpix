# Testing RFpix on the air

Thanks for helping test RFpix, a HamDRM (digital SSTV) program for Windows by KD9DD.
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

1. On the **Settings** page's **Audio** tab, pick your radio's sound input (usually *Microphone (USB Audio CODEC)*)
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
2. **Alpha Testing** page: pick a test and click **Send**. (The sound output is the one picked
   on the Settings page's Audio tab.) RFpix goes on listening on the TX/RX page's
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
