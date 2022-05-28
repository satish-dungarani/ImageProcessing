using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Core.Configuration;
using Nop.Web.Framework.Mvc.ModelBinding;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nop.Plugin.Misc.ImageProcessing
{
    public class ImageProcessingSettings : ISettings
    { 
        public int IPQuality { get; set; }
    }
}
