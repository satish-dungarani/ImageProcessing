using Nop.Core;
using Nop.Plugin.Misc.ImageProcessing;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Plugins;
using System;
using System.Collections.Generic;

namespace Nop.Plugin.ImageProcessing
{
    public class ImageProcessingProvider : BasePlugin, IMiscPlugin
    {

        #region Fields
         
        private readonly ILocalizationService _localizationService; 
        private readonly IStoreContext _storeContext; 
        private readonly IWebHelper _webHelper;
        private readonly ISettingService _settingService;
        #endregion

        #region Ctor

        public ImageProcessingProvider( 
            ILocalizationService localizationService, 
            IStoreContext storeContext, 
            IWebHelper webHelper, 
            ISettingService settingService
            )
        { 
            _localizationService = localizationService; 
            _storeContext = storeContext; 
            _webHelper = webHelper;
            _settingService = settingService;
        }

        #endregion
         
        #region Method      

        /// <summary>
        /// Gets a configuration page URL
        /// </summary>
        public override string GetConfigurationPageUrl()
        {
            return $"{_webHelper.GetStoreLocation()}Admin/ImageProcessing/Configure";
        }

        /// <summary>
        /// Install the plugin
        /// </summary>
        public override void Install()
        {
            //settings
            _settingService.SaveSetting(new ImageProcessingSettings() { IPQuality = 30});

            //locales
            _localizationService.AddPluginLocaleResource(new Dictionary<string, string>
            {
                ["Plugins.Misc.ImageProcessing.IPQuality"] = "Quality",
                ["Plugins.Misc.ImageProcessing.IPQuality.Hint"] = "Select image quality as percentages.",
                ["Plugins.Misc.ImageProcessing.IPMutate"] = "Mutate",
                ["Plugins.Misc.ImageProcessing.IPMutate.Hint"] = "It will apply Image Processing on already uploaded images.",
                ["Plugins.Misc.ImageProcessing.Apply"] = "Apply"
            });

            base.Install();
        }

        /// <summary>
        /// Uninstall the plugin
        /// </summary>
        public override void Uninstall()
        {
            //settings
            _settingService.DeleteSetting<ImageProcessingSettings>();
            //locales
            _localizationService.DeletePluginLocaleResources("Plugins.Misc.ImageProcessing");

            base.Uninstall();
        }
        #endregion
    }
}
