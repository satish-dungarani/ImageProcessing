using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Core;
using Nop.Plugin.Misc.ImageProcessing.Models;
using Nop.Plugin.Misc.ImageProcessing.Services;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Messages;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Misc.ImageProcessing.Controllers
{
    [Area(AreaNames.Admin)]
    [AuthorizeAdmin]
    [AutoValidateAntiforgeryToken]
    public class ImageProcessingController : BasePluginController
    {
        #region Fields

        private readonly IPermissionService _permissionService;
        private readonly ISettingService _settingService;
        private readonly ILocalizationService _localizationService;
        private readonly INotificationService _notificationService;
        private readonly IStoreContext _storeContext;
        private readonly IImageProcessingPictureService _iImageProcessingPictureService;

        #endregion

        #region Ctor
        public ImageProcessingController(
               IPermissionService permissionService,
               ILocalizationService localizationService,
               ISettingService settingService,
               INotificationService notificationService,
               IStoreContext storeContext,
               IImageProcessingPictureService iImageProcessingPictureService)
        {
            _permissionService = permissionService;
            _settingService = settingService;
            _notificationService = notificationService;
            _localizationService = localizationService;
            _storeContext = storeContext;
            _iImageProcessingPictureService = iImageProcessingPictureService;
        }
        #endregion

        #region Methods

        public IActionResult Configure()
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManagePlugins))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = _storeContext.ActiveStoreScopeConfiguration;
            var imageProcessingSettings = _settingService.LoadSetting<ImageProcessingSettings>(storeScope);

            var model = new ConfigurationModel
            {
                IPQuality = imageProcessingSettings.IPQuality
            };
            model.AvailableQualities.Add(new SelectListItem { Text = "10", Value = "10" });
            model.AvailableQualities.Add(new SelectListItem { Text = "20", Value = "20" });
            model.AvailableQualities.Add(new SelectListItem { Text = "30", Value = "30", Selected = true });
            model.AvailableQualities.Add(new SelectListItem { Text = "40", Value = "40" });
            model.AvailableQualities.Add(new SelectListItem { Text = "50", Value = "50" });

            return View("~/Plugins/Misc.ImageProcessing/Views/Configure.cshtml", model);
        }

        [HttpPost]
        [AutoValidateAntiforgeryToken]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public IActionResult Configure(ConfigurationModel model)
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManagePlugins))
                return AccessDeniedView();

            if (!ModelState.IsValid)
                return Configure();

            //load settings for a chosen store scope
            var storeScope = _storeContext.ActiveStoreScopeConfiguration;
            var imageProcessingSettings = _settingService.LoadSetting<ImageProcessingSettings>(storeScope);

            //save settings
            imageProcessingSettings.IPQuality = model.IPQuality;

            _settingService.SaveSetting(imageProcessingSettings);

            //now clear settings cache
            _settingService.ClearCache();

            _notificationService.SuccessNotification(_localizationService.GetResource("Admin.Plugins.Saved"));
            return Configure();
        }

        public IActionResult Mutation()
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManagePlugins))
                return AccessDeniedView();

            if (!ModelState.IsValid)
                return Configure();
            _iImageProcessingPictureService.ApplyMutation();

            //now clear settings cache
            _settingService.ClearCache();

            _notificationService.SuccessNotification(_localizationService.GetResource("Admin.Plugins.Saved"));
            return Configure();
        }

        #endregion
    }
}
