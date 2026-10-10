// drmrxwav: decode a HamDRM WAV with QSSTV's own receiver, headless (verification only).
// Usage: drmrxwav <in.wav (48 kHz 16-bit mono)> <output folder>
#include <QApplication>
#include <QThread>
#include <QFile>
#include <QDir>
#include <cstdio>
#include <vector>
#include "appglobal.h"
#include "configparams.h"
#include "dispatch/dispatchevents.h"
#include "drmrx/drmrx.h"
#include "dsp/downsamplefilter.h"
#include "logbook/logbook.h"

// Stand-in for QSSTV's GUI dispatcher: acknowledge every event so the decoder never blocks.
class FakeDispatcher : public QObject {
public:
  bool event(QEvent *e) override {
    if (e->type() >= QEvent::User) static_cast<baseEvent *>(e)->setDone();
    return true;
  }
};

// Stand-in logbook (QSSTV's real one talks to a rig and an external logger).
logBook::logBook() {}
void logBook::logQSO(QString, QString, QString) {}
void logBook::getFrequency() {}
void logBook::setParam(eIndex, QString) {}

class Worker : public QThread {
public:
  std::vector<short> s;
  void run() override {
    drmRx rx;
    rx.init();
    downsampleFilter ds(DOWNSAMPLESIZE, true);
    ds.init();
    std::vector<short> blk(DOWNSAMPLESIZE);
    for (size_t pos = 0; pos + DOWNSAMPLESIZE <= s.size(); pos += DOWNSAMPLESIZE) {
      std::copy(s.begin() + pos, s.begin() + pos + DOWNSAMPLESIZE, blk.begin());
      ds.downSample4(blk.data());
      rx.run(ds.filteredDataPtr());
    }
  }
};

int main(int argc, char *argv[])
{
  QApplication app(argc, argv);
  if (argc < 3) { fprintf(stderr, "usage: drmrxwav in.wav outdir\n"); return 2; }
  QFile f(argv[1]);
  if (!f.open(QIODevice::ReadOnly)) return 1;
  QByteArray b = f.readAll();
  int dataPos = b.indexOf("data") + 8;
  Worker w;
  w.s.resize((b.size() - dataPos) / 2);
  memcpy(w.s.data(), b.constData() + dataPos, w.s.size() * 2);
  w.s.insert(w.s.end(), 48000 * 3, 0);          // extra silence to flush the decoder

  QDir().mkpath(argv[2]);
  rxDRMImagesPath = argv[2];
  logBookPtr = new logBook;
  FakeDispatcher fd;
  dispatcherPtr = reinterpret_cast<dispatcher *>(&fd);

  QObject::connect(&w, &QThread::finished, &app, &QApplication::quit);
  w.start();
  app.exec();
  for (const QString &n : QDir(argv[2]).entryList(QDir::Files))
    printf("decoded: %s (%lld bytes)\n", qPrintable(n), QFileInfo(QDir(argv[2]), n).size());
  return 0;
}
