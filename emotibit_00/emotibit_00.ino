#include <EmotiBit.h>

EmotiBit emotibit;

const size_t MAX_SAMPLES = 16;
float ppgBuffer[MAX_SAMPLES];
float edaBuffer[MAX_SAMPLES];

void setup() {
  Serial.begin(2000000);
  while (!Serial) { delay(10); }

  uint8_t status = emotibit.setup("plotter_test");
  if (status != 0) {
    Serial.print("ERR:");
    Serial.println(status);
    while (true) { delay(1000); }
  }
}

void loop() {
  emotibit.update();

  uint32_t tsPpg = 0;
  uint32_t tsEda = 0;

  size_t ppgCount = emotibit.readData(EmotiBit::DataType::PPG_INFRARED, ppgBuffer, MAX_SAMPLES, tsPpg);
  size_t edaCount = emotibit.readData(EmotiBit::DataType::EDA, edaBuffer, MAX_SAMPLES, tsEda);

  size_t count = (ppgCount < edaCount) ? ppgCount : edaCount;

  for (size_t i = 0; i < count; i++) {
    Serial.print("PI:");
    Serial.print(ppgBuffer[i], 2);
    Serial.print(",EA:");
    Serial.println(edaBuffer[i], 6);
  }

  delay(5);
}
