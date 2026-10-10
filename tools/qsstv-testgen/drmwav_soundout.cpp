// Replacement for QSSTV's csoundout.cpp: capture DRM transmit samples instead of sending them to a sound card.
#include "drmtx/common/csoundout.h"
#include <vector>

extern std::vector<short> g_samples;

CSoundOut::CSoundOut() {}
CSoundOut::~CSoundOut() {}

bool CSoundOut::Write(CVector<_SAMPLE> &psData)
{
  // psData holds interleaved stereo (L,R,L,R...); keep the left channel.
  for (int i = 0; i + 1 < psData.Size(); i += 2) g_samples.push_back(psData[i]);
  return false;
}
