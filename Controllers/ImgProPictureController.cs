using ImageProcessor;
using ImageProcessor.Plugins.WebP.Imaging.Formats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nop.Services.Media;
using System.IO;
using System.Linq;

namespace Nop.Web.Areas.Admin.Controllers
{
    public partial class ImgProPictureController : BaseAdminController
    {
        #region Fields

        private readonly IPictureService _pictureService;
        private readonly IHostingEnvironment _environment;

        #endregion

        #region Ctor

        public ImgProPictureController(IPictureService pictureService,
            IHostingEnvironment environment)
        {
            _pictureService = pictureService;
            _environment = environment;
        }

        #endregion

        #region Methods

        [HttpPost]
        //do not validate request token (XSRF)
        [IgnoreAntiforgeryToken]
        public virtual IActionResult AsyncUpload(IFormFile img)
        {
             
            if (img == null)
            {
                return Json(new
                {
                    success = false,
                    message = "No file uploaded"
                });
            }

            const string qqFileNameParameter = "qqfilename";

            var qqFileName = Request.Form.ContainsKey(qqFileNameParameter)
                ? Request.Form[qqFileNameParameter].ToString()
                : string.Empty;

            imageproccessing(img);

            var picture = _pictureService.InsertPicture(img, qqFileName);


            //when returning JSON the mime-type must be set to text/plain
            //otherwise some browsers will pop-up a "Save As" dialog.
            return Json(new
            {
                success = true,
                pictureId = picture.Id,
                imageUrl = _pictureService.GetPictureUrl(ref picture, 100)
            });
        }

        private void imageproccessing(IFormFile img)
        {
            int quality = 20;
            // Check if valid image type (can be extended with more rigorous checks)
            //if (image == null) return View();
            //if (image.Length < 0) return View();
            //string[] allowedImageTypes = new string[] { "image/jpeg", "image/png", };
            //if (!allowedImageTypes.Contains(image.ContentType.ToLower())) return View();

            string imagesPath = Path.Combine(_environment.WebRootPath, "images");
            string webPFileName = Path.GetFileNameWithoutExtension(img.FileName) + ".webp";
            string webPImagePath = Path.Combine(imagesPath, webPFileName);

            if (!Directory.Exists(imagesPath))
                Directory.CreateDirectory(imagesPath);

            //using (var normalFileStream = new FileStream(Path.Combine(imagesPath, image.FileName), FileMode.Create))
            //{
            //    image.CopyTo(normalFileStream);
            //}

            // Then save in WebP format
            using (var webPFileStream = new FileStream(webPImagePath, FileMode.Create))
            {
                using (ImageFactory imageFactory = new ImageFactory(preserveExifData: false))
                {
                    imageFactory.Load(img.OpenReadStream())
                                .Format(new WebPFormat())
                                .Quality(quality)
                                .Save(webPFileStream);
                }
            }
        }
         
        #endregion
    }
}