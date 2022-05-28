using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Web.Framework.Models;
using Nop.Web.Framework.Mvc.ModelBinding;
using System.Collections.Generic;

namespace Nop.Plugin.Misc.ImageProcessing.Models
{
    public class ConfigurationModel : BaseNopModel
    {
        public ConfigurationModel()
        {
            AvailableQualities = new List<SelectListItem>();
        }

        public int ActiveStoreScopeConfiguration { get; set; }
        public IList<SelectListItem> AvailableQualities { get; set; }

        [NopResourceDisplayName("Plugins.Misc.ImageProcessing.IPQuality")]
        public int IPQuality { get; set; }

    }
}