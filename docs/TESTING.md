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

1. On the **Settings** page, pick your radio's sound input (usually *Microphone (USB Audio CODEC)*)
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

1. **Radio** page: pick your radio, its COM port and how it's keyed (CAT, RTS, DTR or VOX),
   then **Connect** and **Test PTT**. (Use a dummy load or low power for the PTT test.)
2. **Alpha Testing** page: type your callsign, pick a test, and click **Send**. (The sound
   output is the one picked on the Settings page.) RFpix goes on listening on the TX/RX page's
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

QSSTV and EasyPal users can receive the tests too (they're ordinary HamDRM pictures);
just tell the sender which ones arrived.

73, Dan KD9DD
