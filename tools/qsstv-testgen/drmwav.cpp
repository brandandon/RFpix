// drmwav: headless test-signal generator built from QSSTV's own HamDRM transmitter.
// Usage: drmwav <input file> <output.wav> <mode A|B|E> <bw 2.2|2.5> <qam 4|16|64>
//               <interleave long|short> <protection high|low> <rs 0..4> [callsign]
// Writes 48 kHz, 16-bit mono WAV with 1 s of silence before and after the signal.
#include <QApplication>
#include <QFile>
#include <QFileInfo>
#include <cstdio>
#include <vector>
#include "drmtx/drmtransmitter.h"
#include "drmtx/drmparams.h"
#include "utils/reedsolomoncoder.h"

std::vector<short> g_samples;   // filled by CSoundOut::Write (drmwav_soundout.cpp)

static void putLE(FILE *f, unsigned v, int n) { for (int i = 0; i < n; i++) fputc((v >> (8 * i)) & 0xFF, f); }

int main(int argc, char *argv[])
{
  QApplication app(argc, argv);
  if (argc < 9) { fprintf(stderr, "usage: drmwav in out.wav A|B|E 2.2|2.5 4|16|64 long|short high|low rs0-4 [call]\n"); return 2; }
  QString in = argv[1], out = argv[2];
  QFile f(in);
  if (!f.open(QIODevice::ReadOnly)) { fprintf(stderr, "cannot read %s\n", argv[1]); return 1; }
  QByteArray ba = f.readAll();
  QFileInfo fi(in);

  drmTxParams p;
  QString m = argv[3];
  p.robMode = (m == "A") ? 0 : (m == "B") ? 1 : 2;
  p.bandwith = (QString(argv[4]) == "2.2") ? 0 : 1;          // QSSTV: 0 = 2.2 kHz, 1 = 2.5 kHz
  int q = atoi(argv[5]); p.qam = (q == 4) ? 0 : (q == 16) ? 1 : 2;
  p.interleaver = (QString(argv[6]) == "long") ? 0 : 1;
  p.protection = (QString(argv[7]) == "high") ? 0 : 1;
  p.reedSolomon = atoi(argv[8]);
  p.callsign = (argc > 9) ? argv[9] : "RFPIX";

  drmTransmitter tx;
  eRSType rs = (eRSType)p.reedSolomon;
  if (rs != RSTNONE) {
    reedSolomonCoder rsd;
    rsd.encode(ba, fi.suffix(), rs);
    tx.init(&ba, fi.baseName(), rsTypeStr[rs], p);
  } else {
    tx.init(&ba, fi.baseName(), fi.suffix(), p);
  }
  const int lead = 48000;                       // 1 s silence before
  g_samples.assign(lead, 0);
  tx.start(true);                               // runs until every segment (+5 extra blocks) is sent
  g_samples.insert(g_samples.end(), 48000, 0);  // 1 s silence after

  FILE *o = fopen(argv[2], "wb");
  if (!o) { fprintf(stderr, "cannot write %s\n", argv[2]); return 1; }
  unsigned dataBytes = g_samples.size() * 2;
  fwrite("RIFF", 1, 4, o); putLE(o, 36 + dataBytes, 4); fwrite("WAVE", 1, 4, o);
  fwrite("fmt ", 1, 4, o); putLE(o, 16, 4); putLE(o, 1, 2); putLE(o, 1, 2);
  putLE(o, 48000, 4); putLE(o, 96000, 4); putLE(o, 2, 2); putLE(o, 16, 2);
  fwrite("data", 1, 4, o); putLE(o, dataBytes, 4);
  fwrite(g_samples.data(), 2, g_samples.size(), o);
  fclose(o);
  printf("%s: %d bytes in, %.1f s audio, est %.1f s\n", argv[2], (int)ba.size(),
         g_samples.size() / 48000.0, tx.getDuration());
  return 0;
}
