// make sure you download the correct board from the board manager
// the R4 supports 14 bit, but defaults to 10 for compatability

const int GSR=A0;
int sensorValue=0;
int gsr_average=0;

void setup(){
  Serial.begin(115200);
}

void loop(){
  long sum=0;
  for(int i=0;i<10;i++)           //Average the 10 measurements to remove the glitch
      {
      sensorValue=analogRead(GSR);
      sum += sensorValue;
      delay(5);
      }
   gsr_average = sum/10;
   Serial.println(gsr_average);
}