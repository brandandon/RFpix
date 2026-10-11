// dumpcells: print QSSTV's transmit cell map (cell kinds + pilot values) for one mode/bandwidth.
#include <QApplication>
#include <cstdio>
#include "drmtx/common/ofdmcellmapping/CellMappingTable.h"
int main(int argc, char *argv[])
{
  QApplication app(argc, argv);
  const char *names[] = {"A", "B", "E"};
  ERobMode modes[] = {RM_ROBUSTNESS_MODE_A, RM_ROBUSTNESS_MODE_B, RM_ROBUSTNESS_MODE_E};
  ESpecOcc bws[] = {SO_0, SO_1};
  for (int m = 0; m < 3; m++)
    for (int b = 0; b < 2; b++) {
      CCellMappingTable t;
      t.MakeTable(modes[m], bws[b]);
      printf("MODE %s BW %d KMIN %d CARRIERS %d SYMBOLS %d\n", names[m], b, t.iCarrierKmin, t.iNumCarrier, t.iNumSymbolsPerSuperframe);
      for (int s = 0; s < t.iNumSymbolsPerSuperframe; s++)
        for (int c = 0; c < t.iNumCarrier; c++) {
          int f = t.matiMapTab[s][c];
          _COMPLEX v = t.matcPilotCells[s][c];
          printf("%d %d %d %.6f %.6f\n", s, c + t.iCarrierKmin, f, v.real(), v.imag());
        }
    }
  return 0;
}
