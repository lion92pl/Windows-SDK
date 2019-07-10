using DJI.WindowsSDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using DJIVideoParser;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace DJIWindowsSDKSample.FPV
{
    public sealed partial class FPVPage : Page
    {
        private DJIVideoParser.Parser videoParser;
        private ConcurrentQueue<(byte[], TimeSpan)> _frames = new ConcurrentQueue<(byte[], TimeSpan)>();
        private MemoryStream _dataStream;
        private TimeSpan _dataStreamTimestamp;
        private System.DateTime? _startTime;

        public FPVPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            InitializeVideoFeedModule();
            await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).SetCameraWorkModeAsync(new CameraWorkModeMsg { value = CameraWorkMode.SHOOT_PHOTO });
            DJISDKManager.Instance.ComponentManager.GetRemoteControllerHandler(0, 0).RCShutterButtonDownChanged += FPVPage_RCShutterButtonDownChanged;
            DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).NewlyGeneratedMediaFileChanged += FPVPage_NewlyGeneratedMediaFileChanged;
        }

        private void FPVPage_NewlyGeneratedMediaFileChanged(object sender, GeneratedMediaFileInfo? value)
        {
            var fileType = value.Value.type;
        }

        private void FPVPage_RCShutterButtonDownChanged(object sender, BoolMsg? value)
        {
            if (value.HasValue && value.Value.value)
            {
                TakePhoto(null, null);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UninitializeVideoFeedModule();
            DJISDKManager.Instance.ComponentManager.GetRemoteControllerHandler(0, 0).RCShutterButtonDownChanged -= FPVPage_RCShutterButtonDownChanged;
            DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).NewlyGeneratedMediaFileChanged += FPVPage_NewlyGeneratedMediaFileChanged;
        }

        private async void InitializeVideoFeedModule()
        {
            //Must in UI thread
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                //Raw data and decoded data listener
                if (videoParser == null)
                {
                    videoParser = new DJIVideoParser.Parser();
                    //videoParser.Initialize(delegate (byte[] data)
                    //{
                    //    //Note: This function must be called because we need DJI Windows SDK to help us to parse frame data.
                    //    return DJISDKManager.Instance.VideoFeeder.ParseAssitantDecodingInfo(0, data);
                    //});
                    //Set the swapChainPanel to display and set the decoded data callback.
                    //videoParser.SetSurfaceAndVideoCallback(0, 0, swapChainPanel, ReceiveDecodedData);
                    var videoDesc = new VideoStreamDescriptor(VideoEncodingProperties.CreateH264());
                    videoDesc.EncodingProperties.FrameRate.Denominator = 1;
                    videoDesc.EncodingProperties.FrameRate.Numerator = 30;
                    var source = new MediaStreamSource(videoDesc);
                    source.BufferTime = TimeSpan.Zero;
                    source.IsLive = true;
                    source.Starting += Source_Starting;
                    source.SampleRequested += Source_SampleRequested;
                    mediaElement.RealTimePlayback = true;
                    mediaElement.SetMediaStreamSource(source);
                    //DJISDKManager.Instance.VideoFeeder.GetPrimaryVideoFeed(0).VideoDataUpdated += OnVideoPush;
                }
                //get the camera type and observe the CameraTypeChanged event.
                //DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).CameraTypeChanged += OnCameraTypeChanged;
                var type = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).GetCameraTypeAsync();
                //OnCameraTypeChanged(this, type.value);
            });
        }

        private async void Source_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            if (_startTime.HasValue)
            {
                //args.Request.SetActualStartPosition((System.DateTime.UtcNow - _startTime.Value));
            }
            DJISDKManager.Instance.VideoFeeder.GetPrimaryVideoFeed(0).VideoDataUpdated += OnVideoPush;
            var deferral = args.Request.GetDeferral();
            while (!_startTime.HasValue)
            {
                await Task.Delay(10);
            }
            await Task.Delay(100);
            deferral.Complete();
        }

        private async void Source_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var deferal = args.Request.GetDeferral();

            var sample = await GetSample(args);
            try
            {
                args.Request.Sample = await MediaStreamSample.CreateFromStreamAsync(sample.stream.AsInputStream(), (uint)sample.stream.Length, sample.timestamp);
            }
            catch (Exception ex)
            {
                try
                {
                    sample = await GetSample(args);
                    args.Request.Sample = await MediaStreamSample.CreateFromStreamAsync(sample.stream.AsInputStream(), (uint)sample.stream.Length, sample.timestamp);
                }
                catch
                {
                    sender.NotifyError(MediaStreamSourceErrorStatus.Other);
                }
            }
            deferal.Complete();
        }

        private async Task<(MemoryStream stream, TimeSpan timestamp)> GetSample(MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_dataStream == null || _dataStream.Length == 0)
            {
                uint progress = 0;
                do
                {
                    args.Request.ReportSampleProgress(Math.Min(progress++, 99));
                    await Task.Delay(20);
                } while (_dataStream == null || _dataStream.Length == 0);

                args.Request.ReportSampleProgress(100);
            }

            var streamTimestamp = _dataStreamTimestamp;
            var stream = _dataStream;
            _dataStream = null;
            stream.Position = 0;

            return (stream, streamTimestamp);
        }

        private void UninitializeVideoFeedModule()
        {
            if (DJISDKManager.Instance.SDKRegistrationResultCode == SDKError.NO_ERROR)
            {
                //videoParser.SetSurfaceAndVideoCallback(0, 0, null, null);
                DJISDKManager.Instance.VideoFeeder.GetPrimaryVideoFeed(0).VideoDataUpdated -= OnVideoPush;
                mediaElement.Stop();
            }
        }

        //raw data
        void OnVideoPush(VideoFeed sender, byte[] bytes)
        {
            if (!_startTime.HasValue)
            {
                _startTime = System.DateTime.UtcNow;
            }

            var stream = _dataStream;
            if (_dataStream == null)
            {
                _dataStream = new MemoryStream();
                _dataStreamTimestamp = System.DateTime.UtcNow - _startTime.Value;
            }

            _dataStream.Write(bytes, 0, bytes.Length);
            //videoParser.PushVideoData(0, 0, bytes, bytes.Length);
            //_frames.Enqueue((bytes, System.DateTime.UtcNow - _startTime.Value));
        }

        //Decode data. Do nothing here. This function would return a bytes array with image data in RGBA format.
        async void ReceiveDecodedData(byte[] data, int width, int height)
        {
        }

        //We need to set the camera type of the aircraft to the DJIVideoParser. After setting camera type, DJIVideoParser would correct the distortion of the video automatically.
        private void OnCameraTypeChanged(object sender, CameraTypeMsg? value)
        {
            if (value != null)
            {
                switch (value.Value.value)
                {
                    case CameraType.MAVIC_2_ZOOM:
                        this.videoParser.SetCameraSensor(AircraftCameraType.Mavic2Zoom);
                        break;
                    case CameraType.MAVIC_2_PRO:
                        this.videoParser.SetCameraSensor(AircraftCameraType.Mavic2Pro);
                        break;
                    default:
                        this.videoParser.SetCameraSensor(AircraftCameraType.Others);
                        break;
                }

            }
        }

        private void TakePhoto(object sender, RoutedEventArgs args)
        {
            DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).StartShootPhotoAsync();
        }
    }
}

