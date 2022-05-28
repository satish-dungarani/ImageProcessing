using Autofac;
using Nop.Core.Configuration;
using Nop.Core.Infrastructure;
using Nop.Core.Infrastructure.DependencyManagement;
using Nop.Plugin.Misc.ImageProcessing.Services;
using Nop.Services.Media;

namespace Nop.Plugin.Misc.ImageProcessing.Infrastructure
{
    public class DependencyRegistrar : IDependencyRegistrar
    {
        public virtual void Register(ContainerBuilder builder, ITypeFinder typeFinder, NopConfig config)
        {
            builder.RegisterType<ImageProcessingPictureService>().As<IPictureService>().InstancePerLifetimeScope();
            builder.RegisterType<ImageProcessingPictureService>().As<IImageProcessingPictureService>().InstancePerLifetimeScope();
        }
        public int Order => 1;
    }
}
