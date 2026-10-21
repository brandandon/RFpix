<p align="center">
  <img src="src/RFpix/Assets/RFpix-logo.png" alt="RFpix: images over the air" width="480">
</p>

# RFpix

Digital SSTV for ham radio: send and receive pictures over the air using **HamDRM**,
the digital image mode used by EasyPal and QSSTV.

RFpix is a free, open-source replacement for EasyPal, built first for our SSTV net.
It aims to be easy to install, easy to use, and compatible with stations still
running EasyPal or QSSTV.

> **Status:** pre-alpha, for on-air testing (Windows only). Download the newest test release from
> [Releases](https://github.com/brandandon/RFpix/releases/latest); how to test it is in
> [docs/TESTING.md](docs/TESTING.md).

## Progress

| Date (2026) | Done |
| --- | --- |
| Oct 19 | Sound-card calibration (milestone 1 complete); IC-9700 data mode by CI-V; repeater time-out and adjustable lead-in in send profiles; received settings shown in boxes; faster page switching; window size remembered (v0.3.5) |
| Oct 20 | Missing-piece repair (BSR), working with EasyPal and QSSTV: ask for the pieces a picture is missing, or send them when asked; the Gallery of received and sent pictures (v0.3.6) |
| Oct 20 | Your own text (WF TXT) and pictures (WF PIC) on the waterfall; the Receive tab can pop out into its own window; signal readings in boxes, quality green or red (v0.3.7) |
| Oct 20 | Analog SSTV: send and receive Martin, Scottie, Robot and PD (14 modes); JPEG copies stamped with the time, and uploading them to a website by FTP, FTPS or SFTP (v0.4.0) |
| Oct 18 | Waterfall text before and after each picture; CW and ID buttons; Settings in Audio, Radio and Misc tabs; Send tab laid out to fit any window size (v0.3.4) |
| Oct 18 | Sending your own pictures (v0.3.3): image optimizer fits a picture to the transmit time (Auto or Manual), original and as-sent side by side with zoom, JPEG 2000, JPEG or AVIF (RFpix-only, smaller), settings drop-downs and saved profiles; test transmissions on their own Alpha Testing page; transparent logo; key frequency marks on the waterfall |
| Oct 17 | TX/RX page with Receive and Send tabs; sound devices on the Settings page; wider picture area; logo above the menu (v0.3.0) |
| Oct 17 | Tests 9 and 10 with Reed-Solomon for weak paths; each test sending judged on its own; test log joins a transmission split by a dropout |
| Oct 17 | First on-air tests with a second station (W9BED): every test came through on the repeater |
| Oct 17 | Windows installer with uninstaller, and public test releases (v0.2.0, v0.2.1) |
| Oct 16 | Test results recognised on the Receive page and saved to a test log (opens in Excel) |
| Oct 16 | Transmit level in dB, adjustable while sending; sound gaps from Windows filled so the decoder keeps its timing |
| Oct 15 | Send page with the built-in test transmissions; HamDRM transmitter whose sound matches QSSTV's sample for sample |
| Oct 13 | Decoder toughened with off-air recordings: signal-quality figure, multi-stage decoding, frame prediction |
| Oct 11 | Radio control through Hamlib: CAT, RTS, DTR or VOX keying, IC-9700 data-mode switching, transmit time limit |
| Oct 11 | Record button; JPEG 2000 pictures shown; the RFpix icon |
| Oct 11 | HamDRM receiver complete: all 15 test recordings decode to files identical to the originals, including RS1–RS4 |
| Oct 10 | Waterfall display, sound device picker and recording playback |

## Planned features

- Missing-segment repair (BSR) for repeater use
- Image gallery, and optional auto-upload to an FTP server
- Linux and Mac versions

## Built with

- C# and .NET
- [Avalonia](https://avaloniaui.net/) for the user interface
- NAudio (WASAPI) for sound on Windows.
- [CoreJ2K](https://github.com/cinderblocks/CoreJ2K) (BSD licence) for JPEG 2000 pictures.
- [Magick.NET](https://github.com/dlemstra/Magick.NET) (ImageMagick, Apache licence) for opening pictures and for JPEG and AVIF.
- [Hamlib](https://hamlib.github.io/) (LGPL licence) for radio control (CAT and PTT).
- [Inno Setup](https://jrsoftware.org/isinfo.php) for the Windows installer.

HamDRM support follows the open-source [QSSTV](https://github.com/ON4QZ/QSSTV)
implementation by Johan Maes, ON4QZ.

## License

RFpix is licensed under the GNU General Public License, version 3. See [LICENSE](LICENSE).
