
00. Setting up the Unity × Github Integration:
https://www.youtube.com/watch?v=qpXxcvS-g3g

01. Arduino IDE - Open Arduino IDE, then go to Sketch > Include Library > Manage Libraries.[github] - Search for  EmotiBit FeatherWing  and install it.[github]

Emotibit:
EmotiBit only supports the 2.4GHz band for WiFi, and phones that broadcast a single dual-band SSID can cause silent connection failures. So:

iPhone: Settings → Personal Hotspot → toggle "Maximize Compatibility" on. This forces the hotspot into a mode older/simpler devices (like the Feather) can join.

If I ship this add a proper readme file - and write in there how you can set up a local chatbot in Unity to ask questions about the codebase as an added measure - or we can also create an llm.md file which explains how the whole sytsm works in very minute detail, so the LLMs can ask questions without wasting a bunch of tokens on needing to go through the code base - only do that if you need to modify code. (We of course can AI generate this)