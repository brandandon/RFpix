// dumpmsc: run QSSTV's HamDRM transmitter and record, frame by frame, the MSC bits going
// into the coder and the cells coming out of it (before time interleaving).
// Usage: dumpmsc <file> <out.txt> <mode A|B|E> <bw 2.2|2.5> <qam 4|16|64> <long|short> <high|low>
#include <QApplication>
#include <QFile>
#include <QFileInfo>
#include <cstdio>
#include <vector>
#include "drmtx/common/CDrmTransmitter.h"
#include "drmrx/drm.h"

std::vector<short> g_samples;   // used by drmwav_soundout.cpp; not needed here


class DumpTx : public CDRMTransmitter {
public:
  void RunDump(FILE *o) {
    TransmParam.bRunThread = true;
    int frame = 0, symbolsPerFrame = TransmParam.CellMappingTable.iNumSymPerFrame, symbol = 0;
    while (TransmParam.bRunThread) {
      AudioSourceEncoder.ProcessData(TransmParam, AudSrcBuf);
      int nBits = AudSrcBuf.GetFillLevel();
      if (nBits > 0) {
        fprintf(o, "FRAME %d BITS %d\n", frame, nBits);
        CVectorEx<_BINARY> *b = AudSrcBuf.QueryWriteBuffer();
        for (int i = 0; i < nBits; i += 8) {
          int byte = 0;
          for (int j = 0; j < 8 && i + j < nBits; j++) byte = (byte << 1) | ((*b)[i + j] & 1);
          fprintf(o, "%02x", byte);
        }
        fprintf(o, "\n");
      }
      MSCMLCEncoder.ProcessData(TransmParam, AudSrcBuf, MLCEncBuf);
      int nCells = MLCEncBuf.GetFillLevel();
      if (nCells > 0) {
        fprintf(o, "CELLS %d\n", nCells);
        CVectorEx<_COMPLEX> *c = MLCEncBuf.QueryWriteBuffer();
        for (int i = 0; i < nCells; i++) fprintf(o, "%.4f %.4f%c", (*c)[i].real(), (*c)[i].imag(), i % 8 == 7 ? '\n' : ' ');
        fprintf(o, "\n");
        frame++;
      }
      SymbInterleaver.ProcessData(TransmParam, MLCEncBuf, IntlBuf);
      GenerateFACData.ReadData(TransmParam, GenFACDataBuf);
      FACMLCEncoder.ProcessData(TransmParam, GenFACDataBuf, FACMapBuf);
      OFDMCellMapping.ProcessData(TransmParam, IntlBuf, FACMapBuf, CarMapBuf);
      OFDMModulation.ProcessData(TransmParam, CarMapBuf, OFDMModBuf);
      TransmitData.WriteData(TransmParam, OFDMModBuf);
      if (stopDRM) TransmParam.bRunThread = false;
    }
  }
};

int main(int argc, char *argv[])
{
  QApplication app(argc, argv);
  if (argc < 8) { fprintf(stderr, "usage\n"); return 2; }
  QFile f(argv[1]); f.open(QIODevice::ReadOnly); QByteArray ba = f.readAll(); QFileInfo fi(argv[1]);
  FILE *o = fopen(argv[2], "w");
  DumpTx tx;
  tx.init_base();
  tx.GetAudSrcEnc()->ClearPicFileNames();
  CParameter *p = tx.GetParameters();
  tx.GetAudSrcEnc()->SetPicFileName(&ba, fi.baseName(), fi.suffix());
  QString m = argv[3];
  p->InitCellMapTable(m == "A" ? RM_ROBUSTNESS_MODE_A : m == "B" ? RM_ROBUSTNESS_MODE_B : RM_ROBUSTNESS_MODE_E,
                      QString(argv[4]) == "2.2" ? SO_0 : SO_1);
  p->SetInterleaverDepth(QString(argv[6]) == "long" ? CParameter::SI_LONG : CParameter::SI_SHORT);
  int q = atoi(argv[5]);
  p->SetMSCCodingScheme(q == 4 ? CS_1_SM : q == 16 ? CS_2_SM : CS_3_SM);
  CMSCProtLev prot; prot.iPartB = QString(argv[7]) == "high" ? 0 : 1;
  p->SetMSCProtLev(prot, false);
  CService s; s.iServiceDescr = 0; s.iServiceID = 0; s.iLanguage = 5; s.strLabel = "RFPIX";
  p->SetServiceParameters(0, s);
  tx.Init();
  stopDRM = false;
  tx.RunDump(o);
  fclose(o);
  return 0;
}
