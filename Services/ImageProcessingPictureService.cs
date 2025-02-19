﻿using ImageProcessor;
using ImageProcessor.Imaging;
using ImageProcessor.Plugins.WebP.Imaging.Formats;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Media;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Services.Caching.Extensions;
using Nop.Services.Catalog;
using Nop.Services.Configuration;
using Nop.Services.Events;
using Nop.Services.Media;
using Nop.Services.Seo;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using static SixLabors.ImageSharp.Configuration;

namespace Nop.Plugin.Misc.ImageProcessing.Services
{
    public partial class ImageProcessingPictureService : IImageProcessingPictureService
    {

        #region Fields

        private readonly INopDataProvider _dataProvider;
        private readonly IDownloadService _downloadService;
        private readonly IEventPublisher _eventPublisher;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly INopFileProvider _fileProvider;
        private readonly IProductAttributeParser _productAttributeParser;
        private readonly IRepository<Picture> _pictureRepository;
        private readonly IRepository<PictureBinary> _pictureBinaryRepository;
        private readonly IRepository<ProductPicture> _productPictureRepository;
        private readonly ISettingService _settingService;
        private readonly IUrlRecordService _urlRecordService;
        private readonly IWebHelper _webHelper;
        private readonly MediaSettings _mediaSettings;
        private readonly IHostingEnvironment _environment;
        private readonly ImageProcessingSettings _imageProcessingSettings;

        #endregion

        #region Ctor

        public ImageProcessingPictureService(INopDataProvider dataProvider,
            IDownloadService downloadService,
            IEventPublisher eventPublisher,
            IHttpContextAccessor httpContextAccessor,
            INopFileProvider fileProvider,
            IProductAttributeParser productAttributeParser,
            IRepository<Picture> pictureRepository,
            IRepository<PictureBinary> pictureBinaryRepository,
            IRepository<ProductPicture> productPictureRepository,
            ISettingService settingService,
            IUrlRecordService urlRecordService,
            IWebHelper webHelper,
            MediaSettings mediaSettings,
            IHostingEnvironment environment,
            ImageProcessingSettings imageProcessingSettings)
        {
            _dataProvider = dataProvider;
            _downloadService = downloadService;
            _eventPublisher = eventPublisher;
            _httpContextAccessor = httpContextAccessor;
            _fileProvider = fileProvider;
            _productAttributeParser = productAttributeParser;
            _pictureRepository = pictureRepository;
            _pictureBinaryRepository = pictureBinaryRepository;
            _productPictureRepository = productPictureRepository;
            _settingService = settingService;
            _urlRecordService = urlRecordService;
            _webHelper = webHelper;
            _mediaSettings = mediaSettings;
            _environment = environment;
            _imageProcessingSettings = imageProcessingSettings;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Gets a data hash from database side
        /// </summary>
        /// <param name="binaryData">Array for a hashing function</param>
        /// <param name="limit">Allowed limit input value</param>
        /// <returns>Data hash</returns>
        /// <remarks>
        /// For SQL Server 2014 (12.x) and earlier, allowed input values are limited to 8000 bytes. 
        /// https://docs.microsoft.com/en-us/sql/t-sql/functions/hashbytes-transact-sql
        /// </remarks>
        [Sql.Expression("CONVERT(VARCHAR(128), HASHBYTES('SHA2_512', SUBSTRING({0}, 0, {1})), 2)", ServerSideOnly = true, Configuration = ProviderName.SqlServer)]
        [Sql.Expression("SHA2({0}, 512)", ServerSideOnly = true, Configuration = ProviderName.MySql)]
        public static string Hash(byte[] binaryData, int limit)
            => throw new InvalidOperationException("This function should be used only in database code");

        /// <summary>
        /// Calculates picture dimensions whilst maintaining aspect
        /// </summary>
        /// <param name="originalSize">The original picture size</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="resizeType">Resize type</param>
        /// <param name="ensureSizePositive">A value indicating whether we should ensure that size values are positive</param>
        /// <returns></returns>
        protected virtual Size CalculateDimensions(Size originalSize, int targetSize,
            ResizeType resizeType = ResizeType.LongestSide, bool ensureSizePositive = true)
        {
            float width, height;

            switch (resizeType)
            {
                case ResizeType.LongestSide:
                    if (originalSize.Height > originalSize.Width)
                    {
                        // portrait
                        width = originalSize.Width * (targetSize / (float)originalSize.Height);
                        height = targetSize;
                    }
                    else
                    {
                        // landscape or square
                        width = targetSize;
                        height = originalSize.Height * (targetSize / (float)originalSize.Width);
                    }

                    break;
                case ResizeType.Width:
                    width = targetSize;
                    height = originalSize.Height * (targetSize / (float)originalSize.Width);
                    break;
                case ResizeType.Height:
                    width = originalSize.Width * (targetSize / (float)originalSize.Height);
                    height = targetSize;
                    break;
                default:
                    throw new Exception("Not supported ResizeType");
            }

            if (!ensureSizePositive)
                return new Size((int)Math.Round(width), (int)Math.Round(height));

            if (width < 1)
                width = 1;
            if (height < 1)
                height = 1;

            //we invoke Math.Round to ensure that no white background is rendered - https://www.nopcommerce.com/boards/topic/40616/image-resizing-bug
            return new Size((int)Math.Round(width), (int)Math.Round(height));
        }

        /// <summary>
        /// Loads a picture from file
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <param name="mimeType">MIME type</param>
        /// <returns>Picture binary</returns>
        protected virtual byte[] LoadPictureFromFile(int pictureId, string mimeType)
        {
            var lastPart = GetFileExtensionFromMimeType(mimeType);
            var fileName = $"{pictureId:0000000}_0.{lastPart}";
            var filePath = GetPictureLocalPath(fileName);

            return _fileProvider.ReadAllBytes(filePath);
        }

        /// <summary>
        /// Save picture on file system
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <param name="pictureBinary">Picture binary</param>
        /// <param name="mimeType">MIME type</param>
        protected virtual void SavePictureInFile(int pictureId, byte[] pictureBinary, string mimeType)
        {
            var lastPart = GetFileExtensionFromMimeType(mimeType);
            var fileName = $"{pictureId:0000000}_0.{lastPart}";
            _fileProvider.WriteAllBytes(GetPictureLocalPath(fileName), pictureBinary);

        }

        /// <summary>
        /// Delete a picture on file system
        /// </summary>
        /// <param name="picture">Picture</param>
        protected virtual void DeletePictureOnFileSystem(Picture picture)
        {
            if (picture == null)
                throw new ArgumentNullException(nameof(picture));

            var lastPart = GetFileExtensionFromMimeType(picture.MimeType);
            var fileName = $"{picture.Id:0000000}_0.{lastPart}";
            var filePath = GetPictureLocalPath(fileName);
            _fileProvider.DeleteFile(filePath);
        }

        /// <summary>
        /// Delete picture thumbs
        /// </summary>
        /// <param name="picture">Picture</param>
        protected virtual void DeletePictureThumbs(Picture picture)
        {
            var filter = $"{picture.Id:0000000}*.*";
            var currentFiles = _fileProvider.GetFiles(_fileProvider.GetAbsolutePath(NopMediaDefaults.ImageThumbsPath), filter, false);
            foreach (var currentFileName in currentFiles)
            {
                var thumbFilePath = GetThumbLocalPath(currentFileName);
                _fileProvider.DeleteFile(thumbFilePath);
            }
        }

        /// <summary>
        /// Get picture (thumb) local path
        /// </summary>
        /// <param name="thumbFileName">Filename</param>
        /// <returns>Local picture thumb path</returns>
        protected virtual string GetThumbLocalPath(string thumbFileName)
        {
            var thumbsDirectoryPath = _fileProvider.GetAbsolutePath(NopMediaDefaults.ImageThumbsPath);

            if (_mediaSettings.MultipleThumbDirectories)
            {
                //get the first two letters of the file name
                var fileNameWithoutExtension = _fileProvider.GetFileNameWithoutExtension(thumbFileName);
                if (fileNameWithoutExtension != null && fileNameWithoutExtension.Length > NopMediaDefaults.MultipleThumbDirectoriesLength)
                {
                    var subDirectoryName = fileNameWithoutExtension.Substring(0, NopMediaDefaults.MultipleThumbDirectoriesLength);
                    thumbsDirectoryPath = _fileProvider.GetAbsolutePath(NopMediaDefaults.ImageThumbsPath, subDirectoryName);
                    _fileProvider.CreateDirectory(thumbsDirectoryPath);
                }
            }

            var thumbFilePath = _fileProvider.Combine(thumbsDirectoryPath, thumbFileName);
            return thumbFilePath;
        }

        /// <summary>
        /// Get images path URL 
        /// </summary>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <returns></returns>
        protected virtual string GetImagesPathUrl(string storeLocation = null)
        {
            var pathBase = _httpContextAccessor.HttpContext.Request.PathBase.Value ?? string.Empty;

            var imagesPathUrl = _mediaSettings.UseAbsoluteImagePath ? storeLocation : $"{pathBase}/";

            imagesPathUrl = string.IsNullOrEmpty(imagesPathUrl)
                ? _webHelper.GetStoreLocation()
                : imagesPathUrl;

            imagesPathUrl += "images/";

            return imagesPathUrl;
        }

        /// <summary>
        /// Get picture (thumb) URL 
        /// </summary>
        /// <param name="thumbFileName">Filename</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <returns>Local picture thumb path</returns>
        protected virtual string GetThumbUrl(string thumbFileName, string storeLocation = null)
        {
            var url = GetImagesPathUrl(storeLocation) + "thumbs/";

            if (_mediaSettings.MultipleThumbDirectories)
            {
                //get the first two letters of the file name
                var fileNameWithoutExtension = _fileProvider.GetFileNameWithoutExtension(thumbFileName);
                if (fileNameWithoutExtension != null && fileNameWithoutExtension.Length > NopMediaDefaults.MultipleThumbDirectoriesLength)
                {
                    var subDirectoryName = fileNameWithoutExtension.Substring(0, NopMediaDefaults.MultipleThumbDirectoriesLength);
                    url = url + subDirectoryName + "/";
                }
            }

            url = url + thumbFileName;
            return url;
        }

        /// <summary>
        /// Get picture local path. Used when images stored on file system (not in the database)
        /// </summary>
        /// <param name="fileName">Filename</param>
        /// <returns>Local picture path</returns>
        protected virtual string GetPictureLocalPath(string fileName)
        {
            return _fileProvider.GetAbsolutePath("images", fileName);
        }

        /// <summary>
        /// Gets the loaded picture binary depending on picture storage settings
        /// </summary>
        /// <param name="picture">Picture</param>
        /// <param name="fromDb">Load from database; otherwise, from file system</param>
        /// <returns>Picture binary</returns>
        protected virtual byte[] LoadPictureBinary(Picture picture, bool fromDb)
        {
            if (picture == null)
                throw new ArgumentNullException(nameof(picture));

            var result = fromDb
                ? GetPictureBinaryByPictureId(picture.Id)?.BinaryData ?? Array.Empty<byte>()
                : LoadPictureFromFile(picture.Id, picture.MimeType);

            return result;
        }

        /// <summary>
        /// Get a value indicating whether some file (thumb) already exists
        /// </summary>
        /// <param name="thumbFilePath">Thumb file path</param>
        /// <param name="thumbFileName">Thumb file name</param>
        /// <returns>Result</returns>
        protected virtual bool GeneratedThumbExists(string thumbFilePath, string thumbFileName)
        {
            return _fileProvider.FileExists(thumbFilePath);
        }

        /// <summary>
        /// Save a value indicating whether some file (thumb) already exists
        /// </summary>
        /// <param name="thumbFilePath">Thumb file path</param>
        /// <param name="thumbFileName">Thumb file name</param>
        /// <param name="mimeType">MIME type</param>
        /// <param name="binary">Picture binary</param>
        protected virtual void SaveThumb(string thumbFilePath, string thumbFileName, string mimeType, byte[] binary)
        {
            //ensure \thumb directory exists
            var thumbsDirectoryPath = _fileProvider.GetAbsolutePath(NopMediaDefaults.ImageThumbsPath);
            _fileProvider.CreateDirectory(thumbsDirectoryPath);

            //save
            _fileProvider.WriteAllBytes(thumbFilePath, binary);

            //ImageProcessorWrite(thumbFilePath, binary);
        }

        /// <summary>
        /// Updates the picture binary data
        /// </summary>
        /// <param name="picture">The picture object</param>
        /// <param name="binaryData">The picture binary data</param>
        /// <returns>Picture binary</returns>
        protected virtual PictureBinary UpdatePictureBinary(Picture picture, byte[] binaryData)
        {
            if (picture == null)
                throw new ArgumentNullException(nameof(picture));

            var pictureBinary = GetPictureBinaryByPictureId(picture.Id);

            var isNew = pictureBinary == null;

            if (isNew)
                pictureBinary = new PictureBinary
                {
                    PictureId = picture.Id
                };

            pictureBinary.BinaryData = binaryData;

            if (isNew)
            {
                _pictureBinaryRepository.Insert(pictureBinary);

                //event notification
                _eventPublisher.EntityInserted(pictureBinary);
            }
            else
            {
                _pictureBinaryRepository.Update(pictureBinary);

                //event notification
                _eventPublisher.EntityUpdated(pictureBinary);
            }

            return pictureBinary;
        }

        /// <summary>
        /// Encode the image into a byte array in accordance with the specified image format
        /// </summary>
        /// <typeparam name="TPixel">Pixel data type</typeparam>
        /// <param name="image">Image data</param>
        /// <param name="imageFormat">Image format</param>
        /// <param name="quality">Quality index that will be used to encode the image</param>
        /// <returns>Image binary data</returns>
        protected virtual byte[] EncodeImage<TPixel>(Image<TPixel> image, IImageFormat imageFormat, int? quality = null)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            using var stream = new MemoryStream();
            var imageEncoder = Default.ImageFormatsManager.FindEncoder(imageFormat);
            switch (imageEncoder)
            {
                case JpegEncoder jpegEncoder:
                    jpegEncoder.Subsample = JpegSubsample.Ratio444;
                    jpegEncoder.Quality = quality ?? _mediaSettings.DefaultImageQuality;
                    jpegEncoder.Encode(image, stream);
                    break;

                case PngEncoder pngEncoder:
                    pngEncoder.ColorType = PngColorType.RgbWithAlpha;
                    pngEncoder.Encode(image, stream);
                    break;

                case BmpEncoder bmpEncoder:
                    bmpEncoder.BitsPerPixel = BmpBitsPerPixel.Pixel32;
                    bmpEncoder.Encode(image, stream);
                    break;

                case GifEncoder gifEncoder:
                    gifEncoder.Encode(image, stream);
                    break;

                default:
                    imageEncoder.Encode(image, stream);
                    break;
            }

            return stream.ToArray();
        }

        #endregion

        #region Getting picture local path/URL methods

        /// <summary>
        /// Returns the file extension from mime type.
        /// </summary>
        /// <param name="mimeType">Mime type</param>
        /// <returns>File extension</returns>
        public virtual string GetFileExtensionFromMimeType(string mimeType)
        {
            if (mimeType == null)
                return null;

            var parts = mimeType.Split('/');
            var lastPart = parts[parts.Length - 1];
            switch (lastPart)
            {
                case "pjpeg":
                    lastPart = "jpg";
                    break;
                case "x-png":
                    lastPart = "png";
                    break;
                case "x-icon":
                    lastPart = "ico";
                    break;
            }

            return lastPart;
        }

        /// <summary>
        /// Gets the loaded picture binary depending on picture storage settings
        /// </summary>
        /// <param name="picture">Picture</param>
        /// <returns>Picture binary</returns>
        public virtual byte[] LoadPictureBinary(Picture picture)
        {
            return LoadPictureBinary(picture, StoreInDb);
        }

        /// <summary>
        /// Get picture SEO friendly name
        /// </summary>
        /// <param name="name">Name</param>
        /// <returns>Result</returns>
        public virtual string GetPictureSeName(string name)
        {
            return _urlRecordService.GetSeName(name, true, false);
        }

        /// <summary>
        /// Gets the default picture URL
        /// </summary>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="defaultPictureType">Default picture type</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <returns>Picture URL</returns>
        public virtual string GetDefaultPictureUrl(int targetSize = 0,
            PictureType defaultPictureType = PictureType.Entity,
            string storeLocation = null)
        {
            var defaultImageFileName = defaultPictureType switch
            {
                PictureType.Avatar => _settingService.GetSettingByKey("Media.Customer.DefaultAvatarImageName", NopMediaDefaults.DefaultAvatarFileName),
                _ => _settingService.GetSettingByKey("Media.DefaultImageName", NopMediaDefaults.DefaultImageFileName),
            };
            var filePath = GetPictureLocalPath(defaultImageFileName);
            if (!_fileProvider.FileExists(filePath))
            {
                return string.Empty;
            }

            if (targetSize == 0)
            {
                var url = GetImagesPathUrl(storeLocation) + defaultImageFileName;

                return url;
            }
            else
            {
                var fileExtension = _fileProvider.GetFileExtension(filePath);
                var thumbFileName = $"{_fileProvider.GetFileNameWithoutExtension(filePath)}_{targetSize}{fileExtension}";
                var thumbFilePath = GetThumbLocalPath(thumbFileName);
                if (!GeneratedThumbExists(thumbFilePath, thumbFileName))
                {
                    //using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(filePath, out var imageFormat);
                    //image.Mutate(imageProcess => imageProcess.Resize(new ResizeOptions
                    //{
                    //    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                    //    Size = CalculateDimensions(image.Size(), targetSize)
                    //}));
                    //var pictureBinary = EncodeImage(image, imageFormat);

                    byte[] binaryImg = File.ReadAllBytes(filePath);
                    var pictureBinary = ResizeImageProcessor(binaryImg, targetSize);
                    SaveThumb(thumbFilePath, thumbFileName, "image/webp", pictureBinary);
                }

                var url = GetThumbUrl(thumbFileName, storeLocation);
                return url;
            }
        }

        /// <summary>
        /// Get a picture URL
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="showDefaultPicture">A value indicating whether the default picture is shown</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <param name="defaultPictureType">Default picture type</param>
        /// <returns>Picture URL</returns>
        public virtual string GetPictureUrl(int pictureId,
            int targetSize = 0,
            bool showDefaultPicture = true,
            string storeLocation = null,
            PictureType defaultPictureType = PictureType.Entity)
        {
            var picture = GetPictureById(pictureId);
            return GetPictureUrl(ref picture, targetSize, showDefaultPicture, storeLocation, defaultPictureType);
        }

        /// <summary>
        /// Get a picture URL
        /// </summary>
        /// <param name="picture">Reference instance of Picture</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="showDefaultPicture">A value indicating whether the default picture is shown</param>
        /// <param name="storeLocation">Store location URL; null to use determine the current store location automatically</param>
        /// <param name="defaultPictureType">Default picture type</param>
        /// <returns>Picture URL</returns>
        public virtual string GetPictureUrl(ref Picture picture,
            int targetSize = 0,
            bool showDefaultPicture = true,
            string storeLocation = null,
            PictureType defaultPictureType = PictureType.Entity)
        {
            var defaultImageFileName = defaultPictureType switch
            {
                PictureType.Avatar => _settingService.GetSettingByKey("Media.Customer.DefaultAvatarImageName", NopMediaDefaults.DefaultAvatarFileName),
                _ => _settingService.GetSettingByKey("Media.DefaultImageName", NopMediaDefaults.DefaultImageFileName),
            };
            var filePath = GetPictureLocalPath(defaultImageFileName);

            if (picture == null)
                return showDefaultPicture ? GetDefaultPictureUrl(targetSize, defaultPictureType, storeLocation) : string.Empty;

            byte[] pictureBinary = null;
            if (picture.IsNew)
            {
                DeletePictureThumbs(picture);
                pictureBinary = LoadPictureBinary(picture);

                if ((pictureBinary?.Length ?? 0) == 0)
                    return showDefaultPicture ? GetDefaultPictureUrl(targetSize, defaultPictureType, storeLocation) : string.Empty;

                //we do not validate picture binary here to ensure that no exception ("Parameter is not valid") will be thrown
                picture = UpdatePicture(picture.Id,
                    pictureBinary,
                    picture.MimeType,
                    picture.SeoFilename,
                    picture.AltAttribute,
                    picture.TitleAttribute,
                    false,
                    false);
            }

            var seoFileName = picture.SeoFilename; // = GetPictureSeName(picture.SeoFilename); //just for sure

            var lastPart = GetFileExtensionFromMimeType(picture.MimeType);
            string thumbFileName;
            if (targetSize == 0)
            {
                thumbFileName = !string.IsNullOrEmpty(seoFileName)
                    ? $"{picture.Id:0000000}_{seoFileName}.{lastPart}"
                    : $"{picture.Id:0000000}.{lastPart}";
            }
            else
            {
                thumbFileName = !string.IsNullOrEmpty(seoFileName)
                    ? $"{picture.Id:0000000}_{seoFileName}_{targetSize}.{lastPart}"
                    : $"{picture.Id:0000000}_{targetSize}.{lastPart}";
            }

            var thumbFilePath = GetThumbLocalPath(thumbFileName);

            //the named mutex helps to avoid creating the same files in different threads,
            //and does not decrease performance significantly, because the code is blocked only for the specific file.
            using (var mutex = new Mutex(false, thumbFileName))
            {
                if (GeneratedThumbExists(thumbFilePath, thumbFileName))
                    return GetThumbUrl(thumbFileName, storeLocation);

                mutex.WaitOne();

                //check, if the file was created, while we were waiting for the release of the mutex.
                if (!GeneratedThumbExists(thumbFilePath, thumbFileName))
                {
                    pictureBinary ??= LoadPictureBinary(picture);

                    if ((pictureBinary?.Length ?? 0) == 0)
                        return showDefaultPicture ? GetDefaultPictureUrl(targetSize, defaultPictureType, storeLocation) : string.Empty;

                    byte[] pictureBinaryResized;
                    if (targetSize != 0)
                    {
                        //resizing required
                        //using var image = SixLabors.ImageSharp.Image.Load(pictureBinary, out var imageFormat);
                        //image.Mutate(imageProcess => imageProcess.Resize(new ResizeOptions
                        //{
                        //    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                        //    Size = CalculateDimensions(image.Size(), targetSize)
                        //}));


                        //pictureBinaryResized = EncodeImage(image, imageFormat);

                        pictureBinaryResized = ResizeImageProcessor(pictureBinary, targetSize);
                    }
                    else
                    {
                        //create a copy of pictureBinary
                        pictureBinaryResized = pictureBinary.ToArray();
                    }

                    SaveThumb(thumbFilePath, thumbFileName, picture.MimeType, pictureBinaryResized);
                }

                mutex.ReleaseMutex();
            }

            return GetThumbUrl(thumbFileName, storeLocation);
        }

        /// <summary>
        /// Get a picture local path
        /// </summary>
        /// <param name="picture">Picture instance</param>
        /// <param name="targetSize">The target picture size (longest side)</param>
        /// <param name="showDefaultPicture">A value indicating whether the default picture is shown</param>
        /// <returns></returns>
        public virtual string GetThumbLocalPath(Picture picture, int targetSize = 0, bool showDefaultPicture = true)
        {
            var url = GetPictureUrl(ref picture, targetSize, showDefaultPicture);
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            return GetThumbLocalPath(_fileProvider.GetFileName(url));
        }

        #endregion

        #region CRUD methods

        /// <summary>
        /// Gets a picture
        /// </summary>
        /// <param name="pictureId">Picture identifier</param>
        /// <returns>Picture</returns>
        public virtual Picture GetPictureById(int pictureId)
        {
            if (pictureId == 0)
                return null;

            return _pictureRepository.ToCachedGetById(pictureId);
        }

        /// <summary>
        /// Deletes a picture
        /// </summary>
        /// <param name="picture">Picture</param>
        public virtual void DeletePicture(Picture picture)
        {
            if (picture == null)
                throw new ArgumentNullException(nameof(picture));

            //delete thumbs
            DeletePictureThumbs(picture);

            //delete from file system
            if (!StoreInDb)
                DeletePictureOnFileSystem(picture);

            //delete from database
            _pictureRepository.Delete(picture);

            //event notification
            _eventPublisher.EntityDeleted(picture);
        }

        /// <summary>
        /// Gets a collection of pictures
        /// </summary>
        /// <param name="virtualPath">Virtual path</param>
        /// <param name="pageIndex">Current page</param>
        /// <param name="pageSize">Items on each page</param>
        /// <returns>Paged list of pictures</returns>
        public virtual IPagedList<Picture> GetPictures(string virtualPath = "", int pageIndex = 0, int pageSize = int.MaxValue)
        {
            var query = _pictureRepository.Table;

            if (!string.IsNullOrEmpty(virtualPath))
                query = virtualPath.EndsWith('/') ? query.Where(p => p.VirtualPath.StartsWith(virtualPath) || p.VirtualPath == virtualPath.TrimEnd('/')) : query.Where(p => p.VirtualPath == virtualPath);

            query = query.OrderByDescending(p => p.Id);

            return new PagedList<Picture>(query, pageIndex, pageSize);
        }

        /// <summary>
        /// Gets pictures by product identifier
        /// </summary>
        /// <param name="productId">Product identifier</param>
        /// <param name="recordsToReturn">Number of records to return. 0 if you want to get all items</param>
        /// <returns>Pictures</returns>
        public virtual IList<Picture> GetPicturesByProductId(int productId, int recordsToReturn = 0)
        {
            if (productId == 0)
                return new List<Picture>();

            var query = from p in _pictureRepository.Table
                        join pp in _productPictureRepository.Table on p.Id equals pp.PictureId
                        orderby pp.DisplayOrder, pp.Id
                        where pp.ProductId == productId
                        select p;

            if (recordsToReturn > 0)
                query = query.Take(recordsToReturn);

            var pics = query.ToList();
            return pics;
        }

        /// <summary>
        /// Inserts a picture
        /// </summary>
        /// <param name="pictureBinary">The picture binary</param>
        /// <param name="mimeType">The picture MIME type</param>
        /// <param name="seoFilename">The SEO filename</param>
        /// <param name="altAttribute">"alt" attribute for "img" HTML element</param>
        /// <param name="titleAttribute">"title" attribute for "img" HTML element</param>
        /// <param name="isNew">A value indicating whether the picture is new</param>
        /// <param name="validateBinary">A value indicating whether to validated provided picture binary</param>
        /// <returns>Picture</returns>
        public virtual Picture InsertPicture(byte[] pictureBinary, string mimeType, string seoFilename,
            string altAttribute = null, string titleAttribute = null,
            bool isNew = true, bool validateBinary = true)
        {
            mimeType = CommonHelper.EnsureNotNull(mimeType);
            mimeType = CommonHelper.EnsureMaximumLength(mimeType, 20);

            seoFilename = CommonHelper.EnsureMaximumLength(seoFilename, 100);

            //if (validateBinary)
            //    pictureBinary = ValidatePicture(pictureBinary, mimeType);

            var picture = new Picture
            {
                MimeType = mimeType,
                SeoFilename = seoFilename,
                AltAttribute = altAttribute,
                TitleAttribute = titleAttribute,
                IsNew = isNew
            };
            _pictureRepository.Insert(picture);
            UpdatePictureBinary(picture, StoreInDb ? pictureBinary : Array.Empty<byte>());

            if (!StoreInDb)
                SavePictureInFile(picture.Id, pictureBinary, mimeType);

            //event notification
            _eventPublisher.EntityInserted(picture);

            return picture;
        }

        /// <summary>
        /// Inserts a picture
        /// </summary>
        /// <param name="formFile">Form file</param>
        /// <param name="defaultFileName">File name which will be use if IFormFile.FileName not present</param>
        /// <param name="virtualPath">Virtual path</param>
        /// <returns>Picture</returns>
        public virtual Picture InsertPicture(IFormFile formFile, string defaultFileName = "", string virtualPath = "")
        {
            var imgExt = new List<string>
            {
                ".bmp",
                ".gif",
                ".jpeg",
                ".jpg",
                ".jpe",
                ".jfif",
                ".pjpeg",
                ".pjp",
                ".png",
                ".tiff",
                ".tif"
            } as IReadOnlyCollection<string>;

            var fileName = formFile.FileName;
            if (string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(defaultFileName))
                fileName = defaultFileName;

            //remove path (passed in IE)
            fileName = _fileProvider.GetFileName(fileName);

            var contentType = formFile.ContentType;

            var fileExtension = _fileProvider.GetFileExtension(fileName);
            if (!string.IsNullOrEmpty(fileExtension))
                fileExtension = fileExtension.ToLowerInvariant();

            if (imgExt.All(ext => !ext.Equals(fileExtension, StringComparison.CurrentCultureIgnoreCase)))
                return null;

            var picture = InsertPicture(IFileToBinaryImageProcessor(formFile), "image/webp", _fileProvider.GetFileNameWithoutExtension(fileName));

            if (string.IsNullOrEmpty(virtualPath))
                return picture;

            picture.VirtualPath = _fileProvider.GetVirtualPath(virtualPath);
            UpdatePicture(picture);

            return picture;
        }

        /// <summary>
        /// Updates the picture
        /// </summary>
        /// <param name="pictureId">The picture identifier</param>
        /// <param name="pictureBinary">The picture binary</param>
        /// <param name="mimeType">The picture MIME type</param>
        /// <param name="seoFilename">The SEO filename</param>
        /// <param name="altAttribute">"alt" attribute for "img" HTML element</param>
        /// <param name="titleAttribute">"title" attribute for "img" HTML element</param>
        /// <param name="isNew">A value indicating whether the picture is new</param>
        /// <param name="validateBinary">A value indicating whether to validated provided picture binary</param>
        /// <returns>Picture</returns>
        public virtual Picture UpdatePicture(int pictureId, byte[] pictureBinary, string mimeType,
            string seoFilename, string altAttribute = null, string titleAttribute = null,
            bool isNew = true, bool validateBinary = true)
        {
            mimeType = CommonHelper.EnsureNotNull(mimeType);
            mimeType = CommonHelper.EnsureMaximumLength(mimeType, 20);

            seoFilename = CommonHelper.EnsureMaximumLength(seoFilename, 100);

            //if (validateBinary)
            //    pictureBinary = ValidatePicture(pictureBinary, mimeType);

            var picture = GetPictureById(pictureId);
            if (picture == null)
                return null;

            //delete old thumbs if a picture has been changed
            if (seoFilename != picture.SeoFilename)
                DeletePictureThumbs(picture);

            picture.MimeType = mimeType;
            picture.SeoFilename = seoFilename;
            picture.AltAttribute = altAttribute;
            picture.TitleAttribute = titleAttribute;
            picture.IsNew = isNew;

            _pictureRepository.Update(picture);
            UpdatePictureBinary(picture, StoreInDb ? pictureBinary : Array.Empty<byte>());

            if (!StoreInDb)
                SavePictureInFile(picture.Id, pictureBinary, mimeType);

            //event notification
            _eventPublisher.EntityUpdated(picture);

            return picture;
        }

        /// <summary>
        /// Updates the picture
        /// </summary>
        /// <param name="picture">The picture to update</param>
        /// <returns>Picture</returns>
        public virtual Picture UpdatePicture(Picture picture)
        {
            if (picture == null)
                return null;

            var seoFilename = CommonHelper.EnsureMaximumLength(picture.SeoFilename, 100);

            //delete old thumbs if a picture has been changed
            if (seoFilename != picture.SeoFilename)
                DeletePictureThumbs(picture);

            picture.SeoFilename = seoFilename;

            _pictureRepository.Update(picture);
            UpdatePictureBinary(picture, StoreInDb ? GetPictureBinaryByPictureId(picture.Id).BinaryData : Array.Empty<byte>());

            if (!StoreInDb)
                SavePictureInFile(picture.Id, GetPictureBinaryByPictureId(picture.Id).BinaryData, picture.MimeType);

            //event notification
            _eventPublisher.EntityUpdated(picture);

            return picture;
        }

        /// <summary>
        /// Get product picture binary by picture identifier
        /// </summary>
        /// <param name="pictureId">The picture identifier</param>
        /// <returns>Picture binary</returns>
        public virtual PictureBinary GetPictureBinaryByPictureId(int pictureId)
        {
            return _pictureBinaryRepository.Table.FirstOrDefault(pb => pb.PictureId == pictureId);
        }

        /// <summary>
        /// Updates a SEO filename of a picture
        /// </summary>
        /// <param name="pictureId">The picture identifier</param>
        /// <param name="seoFilename">The SEO filename</param>
        /// <returns>Picture</returns>
        public virtual Picture SetSeoFilename(int pictureId, string seoFilename)
        {
            var picture = GetPictureById(pictureId);
            if (picture == null)
                throw new ArgumentException("No picture found with the specified id");

            //update if it has been changed
            if (seoFilename != picture.SeoFilename)
            {
                //update picture
                picture = UpdatePicture(picture.Id,
                    LoadPictureBinary(picture),
                    picture.MimeType,
                    seoFilename,
                    picture.AltAttribute,
                    picture.TitleAttribute,
                    true,
                    false);
            }

            return picture;
        }

        /// <summary>
        /// Validates input picture dimensions
        /// </summary>
        /// <param name="pictureBinary">Picture binary</param>
        /// <param name="mimeType">MIME type</param>
        /// <returns>Picture binary or throws an exception</returns>
        public virtual byte[] ValidatePicture(byte[] pictureBinary, string mimeType)
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pictureBinary, out var imageFormat);
            //resize the image in accordance with the maximum size
            if (Math.Max(image.Height, image.Width) > _mediaSettings.MaximumImageSize)
            {
                image.Mutate(imageProcess => imageProcess.Resize(new ResizeOptions
                {
                    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                    Size = new Size(_mediaSettings.MaximumImageSize)
                }));
            }

            return EncodeImage(image, imageFormat);
        }

        /// <summary>
        /// Get pictures hashes
        /// </summary>
        /// <param name="picturesIds">Pictures Ids</param>
        /// <returns></returns>
        public IDictionary<int, string> GetPicturesHash(int[] picturesIds)
        {
            if (!picturesIds.Any())
                return new Dictionary<int, string>();

            var hashes = _dataProvider.GetTable<PictureBinary>()
                    .Where(p => picturesIds.Contains(p.PictureId))
                    .Select(x => new
                    {
                        x.PictureId,
                        Hash = Hash(x.BinaryData, _dataProvider.SupportedLengthOfBinaryHash)
                    });

            return hashes.ToDictionary(p => p.PictureId, p => p.Hash);
        }

        /// <summary>
        /// Get product picture (for shopping cart and order details pages)
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="attributesXml">Attributes (in XML format)</param>
        /// <returns>Picture</returns>
        public virtual Picture GetProductPicture(Product product, string attributesXml)
        {
            if (product == null)
                throw new ArgumentNullException(nameof(product));

            //first, try to get product attribute combination picture
            var combination = _productAttributeParser.FindProductAttributeCombination(product, attributesXml);
            var combinationPicture = GetPictureById(combination?.PictureId ?? 0);
            if (combinationPicture != null)
                return combinationPicture;

            //then, let's see whether we have attribute values with pictures
            var attributePicture = _productAttributeParser.ParseProductAttributeValues(attributesXml)
                .Select(attributeValue => GetPictureById(attributeValue?.PictureId ?? 0))
                .FirstOrDefault(picture => picture != null);
            if (attributePicture != null)
                return attributePicture;

            //now let's load the default product picture
            var productPicture = GetPicturesByProductId(product.Id, 1).FirstOrDefault();
            if (productPicture != null)
                return productPicture;

            //finally, let's check whether this product has some parent "grouped" product
            if (product.VisibleIndividually || product.ParentGroupedProductId <= 0)
                return null;

            var parentGroupedProductPicture = GetPicturesByProductId(product.ParentGroupedProductId, 1).FirstOrDefault();
            return parentGroupedProductPicture;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets a value indicating whether the images should be stored in data base.
        /// </summary>
        public virtual bool StoreInDb
        {
            get => _settingService.GetSettingByKey("Media.Images.StoreInDB", true);
            set
            {
                //check whether it's a new value
                if (StoreInDb == value)
                    return;

                //save the new setting value
                _settingService.SetSetting("Media.Images.StoreInDB", value);

                var pageIndex = 0;
                const int pageSize = 400;
                try
                {
                    while (true)
                    {
                        var pictures = GetPictures(pageIndex: pageIndex, pageSize: pageSize);
                        pageIndex++;

                        //all pictures converted?
                        if (!pictures.Any())
                            break;

                        foreach (var picture in pictures)
                        {
                            if (!string.IsNullOrEmpty(picture.VirtualPath))
                                continue;

                            var pictureBinary = LoadPictureBinary(picture, !value);

                            //we used the code below before. but it's too slow
                            //let's do it manually (uncommented code) - copy some logic from "UpdatePicture" method
                            /*just update a picture (all required logic is in "UpdatePicture" method)
                            we do not validate picture binary here to ensure that no exception ("Parameter is not valid") will be thrown when "moving" pictures
                            UpdatePicture(picture.Id,
                                          pictureBinary,
                                          picture.MimeType,
                                          picture.SeoFilename,
                                          true,
                                          false);*/
                            if (value)
                                //delete from file system. now it's in the database
                                DeletePictureOnFileSystem(picture);
                            else
                                //now on file system
                                SavePictureInFile(picture.Id, pictureBinary, picture.MimeType);
                            //update appropriate properties
                            UpdatePictureBinary(picture, value ? pictureBinary : Array.Empty<byte>());
                            picture.IsNew = true;
                            //raise event?
                            //_eventPublisher.EntityUpdated(picture);
                        }

                        //save all at once
                        _pictureRepository.Update(pictures);
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        #endregion


        public byte[] IFileToBinaryImageProcessor(IFormFile image)
        {
            using (MemoryStream outStream = new MemoryStream())
            {
                using (ImageFactory imageFactory = new ImageFactory(preserveExifData: true))
                {
                    var img = System.Drawing.Image.FromStream(image.OpenReadStream());
                    var size = new System.Drawing.Size(img.Width, img.Height);

                    ResizeLayer resizeLayer = new ResizeLayer(newCalculateDimensions(size, _mediaSettings.MaximumImageSize), ImageProcessor.Imaging.ResizeMode.Max);

                    imageFactory.Load(image.OpenReadStream())
                                .Resize(resizeLayer)
                                .Format(new WebPFormat())
                                .Quality(_imageProcessingSettings.IPQuality)
                                //.Quality(_mediaSettings.DefaultImageQuality)
                                .Save(outStream);
                }
                return outStream.ToArray();
            }
        }

        private byte[] OtherBinaryToImageProcessorBinary(byte[] inputBinary)
        {
            using (MemoryStream inStream = new MemoryStream(inputBinary))
            {
                using (MemoryStream outStream = new MemoryStream())
                {
                    using (ImageFactory imageFactory = new ImageFactory(preserveExifData: true))
                    {
                        imageFactory.Load(inStream)
                                 .Format(new WebPFormat())
                                 .Quality(_imageProcessingSettings.IPQuality)
                                 .Save(outStream);
                    }
                    return outStream.ToArray();
                }
            }
        }

        public byte[] ResizeImageProcessor(byte[] binary, int targetSize)
        {
            using (MemoryStream inStream = new MemoryStream(binary))
            {
                using (MemoryStream outStream = new MemoryStream())
                {
                    using (ImageFactory imageFactory = new ImageFactory(preserveExifData: true))
                    {
                        var createdImage = imageFactory.Load(inStream);

                        createdImage = createdImage
                               .Resize(new ResizeLayer(newCalculateDimensions(createdImage.Image.Size, targetSize), ImageProcessor.Imaging.ResizeMode.Max))
                               .Save(outStream);
                    }
                    return outStream.ToArray();
                }
            }
        }

        protected virtual System.Drawing.Size newCalculateDimensions(System.Drawing.Size originalSize, int targetSize,
            ResizeType resizeType = ResizeType.LongestSide, bool ensureSizePositive = true)
        {
            float width, height;

            switch (resizeType)
            {
                case ResizeType.LongestSide:
                    if (originalSize.Height > originalSize.Width)
                    {
                        // portrait
                        width = originalSize.Width * (targetSize / (float)originalSize.Height);
                        height = targetSize;
                    }
                    else
                    {
                        // landscape or square
                        width = targetSize;
                        height = originalSize.Height * (targetSize / (float)originalSize.Width);
                    }

                    break;
                case ResizeType.Width:
                    width = targetSize;
                    height = originalSize.Height * (targetSize / (float)originalSize.Width);
                    break;
                case ResizeType.Height:
                    width = originalSize.Width * (targetSize / (float)originalSize.Height);
                    height = targetSize;
                    break;
                default:
                    throw new Exception("Not supported ResizeType");
            }

            if (!ensureSizePositive)
                return new System.Drawing.Size((int)Math.Round(width), (int)Math.Round(height));

            if (width < 1)
                width = 1;
            if (height < 1)
                height = 1;

            //we invoke Math.Round to ensure that no white background is rendered - https://www.nopcommerce.com/boards/topic/40616/image-resizing-bug
            return new System.Drawing.Size((int)Math.Round(width), (int)Math.Round(height));
        }

        public void ApplyMutation()
        {
            MutateDirImagesStoreInDb();
            MutateDirImages();
        }

        protected virtual void MutateDirImages()
        { 
            var path = _environment.WebRootPath + "//images//";
            foreach (DirectoryInfo dir in new DirectoryInfo(path).GetDirectories())
            {
                var dirPath = path + dir.Name;
                foreach (FileInfo file in dir.GetFiles())
                {
                    if (file.Extension.ToLower() == ".jpg" || file.Extension.ToLower() == ".jpeg" || file.Extension.ToLower() == ".gif" || file.Extension.ToLower() == ".png"
                        || file.Extension.ToLower() == ".tiff" || file.Extension.ToLower() == ".tif" || file.Extension.ToLower() == ".bmp" || file.Extension.ToLower() == ".jpe"
                        || file.Extension.ToLower() == ".jfif" || file.Extension.ToLower() == ".pjpeg" || file.Extension.ToLower() == ".pjp")
                    {
                        using (MemoryStream inStream = new MemoryStream(File.ReadAllBytes(file.FullName)))
                        {
                            using (MemoryStream outStream = new MemoryStream())
                            {
                                using (ImageFactory imageFactory = new ImageFactory(preserveExifData: true))
                                {
                                    imageFactory.Load(inStream)
                                             .Format(new WebPFormat())
                                             .Quality(_imageProcessingSettings.IPQuality)
                                             .Save(outStream);
                                }
                                outStream.WriteTo(new FileStream(dirPath + "//" + _fileProvider.GetFileNameWithoutExtension(file.Name) + ".webp", FileMode.Create));
                            }
                        }
                        file.Delete();
                    }
                }
            }
        }

        protected virtual void MutateDirImagesStoreInDb()
        {
            if (!StoreInDb)
            {
                var path = _environment.WebRootPath + "//images//";
                DirectoryInfo dir = new DirectoryInfo(path);
                var dirPath = path + dir.Name;
                foreach (FileInfo file in dir.GetFiles())
                {
                    if (file.Extension.ToLower() == ".jpg" || file.Extension.ToLower() == ".jpeg" || file.Extension.ToLower() == ".gif" || file.Extension.ToLower() == ".png"
                        || file.Extension.ToLower() == ".tiff" || file.Extension.ToLower() == ".tif" || file.Extension.ToLower() == ".bmp" || file.Extension.ToLower() == ".jpe"
                        || file.Extension.ToLower() == ".jfif" || file.Extension.ToLower() == ".pjpeg" || file.Extension.ToLower() == ".pjp")
                    {
                        using (MemoryStream inStream = new MemoryStream(File.ReadAllBytes(file.FullName)))
                        {
                            using (MemoryStream outStream = new MemoryStream())
                            {
                                using (ImageFactory imageFactory = new ImageFactory(preserveExifData: true))
                                {
                                    imageFactory.Load(inStream)
                                             .Format(new WebPFormat())
                                             .Quality(_imageProcessingSettings.IPQuality)
                                             .Save(outStream);
                                }
                                outStream.WriteTo(new FileStream(dirPath + "//" + _fileProvider.GetFileNameWithoutExtension(file.Name) + ".webp", FileMode.Create));
                            }
                        }
                        file.Delete();
                    }
                }
            }
            else
            {
                var pictures = _pictureRepository.Table.Where(x => x.MimeType.ToLower() != "image/webp").ToList();

                foreach (Picture pic in pictures)
                {
                    pic.MimeType = "image/webp";
                    _pictureRepository.Update(pic);
                    var picBinary = _pictureBinaryRepository.Table.Where(x => x.PictureId == pic.Id && x.BinaryData.ToString() != "0x" && x.BinaryData.ToString() != null).FirstOrDefault();

                    if (picBinary != null)
                    {
                        using (MemoryStream inStream = new MemoryStream(picBinary.BinaryData))
                        {
                            using (MemoryStream outStream = new MemoryStream())
                            {
                                using (ImageFactory imageFactory = new ImageFactory(preserveExifData: true))
                                {
                                    imageFactory.Load(inStream)
                                             .Format(new WebPFormat())
                                             .Quality(_imageProcessingSettings.IPQuality)
                                             .Save(outStream);
                                }
                                picBinary.BinaryData =  outStream.GetBuffer();
                                _pictureBinaryRepository.Update(picBinary);
                            }
                        }
                    }

                }

            }
        }

    }
}
