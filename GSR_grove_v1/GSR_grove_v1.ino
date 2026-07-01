/*
  DELPHI — Grove GSR sensor on Arduino UNO R4
  ------------------------------------------------------------
  Based on the Seeed Studio Grove-GSR example. Averages 10 reads to
  remove glitch, same as the original. Only change: baud bumped to
  115200 for headroom. Outputs one integer per line — the raw,
  glitch-averaged ADC reading. All calibration / resistance maths
  happens in Unity, not here.
*/

const int GSR = A0;
int sensorValue = 0;
long sum = 0;

void setup() {
  Serial.begin(115200);
}

void loop() {
  sum = 0;
  for (int i = 0; i < 10; i++) {       // average 10 reads to remove glitch
    sensorValue = analogRead(GSR);
    sum += sensorValue;
    delay(5);
  }
  int gsr_average = sum / 10;
  Serial.println(gsr_average);
}
