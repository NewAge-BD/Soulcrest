# Lists the installed Windows OCR languages (one tag per line), as Soulcrest sees them
# (Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages). Used by the setup; needs no admin rights.
$ErrorActionPreference = 'Stop'
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
[Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag }
