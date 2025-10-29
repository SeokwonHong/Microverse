using Unity.Collections;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation.Runtime.Data
{
    public struct Buffers
    {
        public NativeArray<float2> posCur, posNext;
        public NativeArray<float2> velCur, velNext;
        public NativeArray<byte> species;
        public NativeArray<byte> state;        // 0=Alive 1=Dead 2=Latched
        public NativeArray<float2> latchOffset;  // 라치된 백혈구 오프셋
        public NativeArray<float> latchTTL;     // 라치 잔여
        public NativeArray<float2> posSnap;      // PBD

        // ★ 여기 추가: 전체 스쿼시(강도/방향)
        public NativeArray<float> squash;
        public NativeArray<float2> squashN;

        // ★ 덴트(국소 함몰) 4개
        public NativeArray<float2> dentDir0, dentDir1, dentDir2, dentDir3;
        public NativeArray<float> dentAmp0, dentAmp1, dentAmp2, dentAmp3;

        public void Dispose()
        {
            if (posCur.IsCreated) posCur.Dispose();
            if (posNext.IsCreated) posNext.Dispose();
            if (velCur.IsCreated) velCur.Dispose();
            if (velNext.IsCreated) velNext.Dispose();
            if (species.IsCreated) species.Dispose();
            if (state.IsCreated) state.Dispose();
            if (latchOffset.IsCreated) latchOffset.Dispose();
            if (latchTTL.IsCreated) latchTTL.Dispose();
            if (posSnap.IsCreated) posSnap.Dispose();

            // ★ 추가한 필드도 Dispose
            if (squash.IsCreated) squash.Dispose();
            if (squashN.IsCreated) squashN.Dispose();

            if (dentDir0.IsCreated) dentDir0.Dispose();
            if (dentDir1.IsCreated) dentDir1.Dispose();
            if (dentDir2.IsCreated) dentDir2.Dispose();
            if (dentDir3.IsCreated) dentDir3.Dispose();
            if (dentAmp0.IsCreated) dentAmp0.Dispose();
            if (dentAmp1.IsCreated) dentAmp1.Dispose();
            if (dentAmp2.IsCreated) dentAmp2.Dispose();
            if (dentAmp3.IsCreated) dentAmp3.Dispose();
        }
    }
}
