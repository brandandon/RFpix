# RFpix

Digital SSTV for ham radio: send and receive pictures over the air using **HamDRM**,
the digital image mode used by EasyPal and QSSTV.

RFpix is a free, open-source replacement for EasyPal, built first for our SSTV net.
It aims to be easy to install, easy to use, and compatible with stations still
running EasyPal or QSSTV.

> **Status:** early development. Nothing to download yet.

## Planned features

- Receive and send HamDRM images (all modes: A, B, E; 2.2 / 2.5 kHz; 4/16/64-QAM)
- Waterfall display with signal level and SNR
- Radio control (CAT / PTT) through Hamlib
- Image optimizer: resize and compress to fit a chosen transmit time
- Repeater support: time-out warning, key-up lead-in, missing-segment repair (BSR)
- Image gallery, waterfall ID, and optional auto-upload to an FTP server
- Windows first, then Linux and Mac

## Built with

- C# and .NET
- [Avalonia](https://avaloniaui.net/) for the user interface
- NAudio (WASAPI) for sound on Windows.
- [CoreJ2K](https://github.com/cinderblocks/CoreJ2K) (BSD licence) for JPEG 2000 pictures.
- [Hamlib](https://hamlib.github.io/) (LGPL licence) for radio control (CAT and PTT).

HamDRM support follows the open-source [QSSTV](https://github.com/ON4QZ/QSSTV)
implementation by Johan Maes, ON4QZ.

## License

RFpix is licensed under the GNU General Public License, version 3. See [LICENSE](LICENSE).
