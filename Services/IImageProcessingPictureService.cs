using Nop.Services.Media;

namespace Nop.Plugin.Misc.ImageProcessing.Services
{
    public interface IImageProcessingPictureService : IPictureService
    {
        void ApplyMutation();
    }
}
