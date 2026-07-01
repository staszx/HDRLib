// Copyright (c) Stanislav Popov. All rights reserved.

using System.Diagnostics;
using HDRLib;
using HDRLib.Align;
using HDRLib.Hdr.Debevec;
using HDRLib.PixelProvider.ImageSharp;
using HDRLib.ToneMapping.Settings;
using PhotoProcessor;
using HDRLib.Classifier;
using HDRLib.Gpu;
using HDRLib.ToneMapping;

Console.WriteLine(args[0]);
string input = args[0];
string hdrOut = Path.Combine(args[0], "Hdr");
string singleOut = Path.Combine(args[0], "Single");
SystemHelper.UseAvxState = UseAvxState.Enable;
using var gpu = new GpuContext(2);

if (!Directory.Exists(hdrOut))
{
    Directory.CreateDirectory(hdrOut);
}

if (!Directory.Exists(singleOut))
{
    Directory.CreateDirectory(singleOut);
}

//Hdr process
var result = HdrSeriesSeparator.SeparateHdrSeries(
    input,
    new[] { ".jpg", ".jpeg", ".png" }
);

var processed = 0;
var toneMapperSettings = new NaturalToneMapperSettings()
{
    AutoAdjustEnabled = false,
    ExposureEV = 0f,
    Brightness = 1.05f,
    Contrast = 1.4f,
    Saturation = 1.8f,
   ColorTemperature = -25
    
};


var hdrProcessor = new HDRProcessor<ImageSharpProxy>(toneMapperSettings);
var aligner = ImageAligner.Create();

Console.WriteLine($"HDR count: {result.HdrSeries.Count}");
if (result.HdrSeries.Count > 0)
{
    Console.WriteLine("Start HDR process");
}

foreach (var list in result.HdrSeries)
{

    var loadedImages = Helper.Load(list);

    var sw = new Stopwatch();
    sw.Start();
    aligner.Process(loadedImages);

    var image = hdrProcessor.Process(loadedImages, new HdrImageOptions
    {
        SampleCount = 1000,
        SmoothFactor = 300,
        MotionFilterStrength = 80,
        ToneMapperSettings = toneMapperSettings
    });

    var fileName = Path.GetFileName(list[0]);
    image.SaveAsJpeg(Path.Combine(hdrOut, fileName));
    sw.Stop();
    
    processed++;
    Console.WriteLine($"{processed}.{Path.GetFileName(fileName)}: {sw.Elapsed.TotalSeconds}, sec.");

    //Console.WriteLine(processed);
}

Console.WriteLine($"Single count: {result.SingleImages.Count}");
if (result.SingleImages.Count > 0)
{
    Console.WriteLine("Start single process");
}

//Parallel.ForEach(result.SingleImages, (imagePath)=>
foreach (var imagePath in result.SingleImages)
{
    var sw = new Stopwatch();
    sw.Start();
    var image = new ImageSharpProxy();
    image.Load(imagePath);
    var processor = new SingleImageProcessor(image);
    processor.Process(toneMapperSettings, gpu);
    var outImage = processor.ToImage<ImageSharpProxy>();
    var fileName = Path.GetFileName(imagePath+"_2.jpg");
    outImage.SaveAsJpeg(Path.Combine(singleOut, fileName));
    sw.Stop();
    processed++;
    Console.WriteLine($"{processed}.{Path.GetFileName(fileName)}: {sw.Elapsed.TotalSeconds}, sec.");
}
