﻿using APICore.Common.DTO.Request;
using APICore.Data.Entities;
using APICore.Data.Entities.Enums;
using APICore.Data.UoW;
using APICore.Services.Exceptions;
using DeviceDetectorNET;
using DeviceDetectorNET.Cache;
using DeviceDetectorNET.Parser;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.WindowsAzure.Storage.Blob;
using PasswordGenerator;
using rlcx.suid;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Wangkanai.Detection;

namespace APICore.Services.Impls
{
    class Tokens
    {
        public string Token { get; set; }
        public string RefreshToken { get; set; }
    }
    public class AccountService : IAccountService
    {
        private readonly IConfiguration _configuration;

        private readonly IUnitOfWork _uow;

        private readonly IStringLocalizer<IAccountService> _localizer;
        private readonly IDetection _detection;
        //private readonly CloudBlobClient _blobClient;

        //public AccountService(IConfiguration configuration, IUnitOfWork uow, IStringLocalizer<IAccountService> localizer, IDetection detection, CloudBlobClient blobClient)
        public AccountService(IConfiguration configuration, IUnitOfWork uow, IStringLocalizer<IAccountService> localizer, IDetection detection)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _uow = uow ?? throw new ArgumentNullException(nameof(uow));
            _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
            _detection = detection ?? throw new ArgumentNullException(nameof(detection));
            //_blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        }

        public async Task<(User user, string accessToken, string refreshToken)> LoginAsync(LoginRequest loginRequest)
        {
            var hashedPass = GetSha256Hash(loginRequest.Password);

            var user = await _uow.UserRepository.FindBy(u => u.Email == loginRequest.Email).FirstOrDefaultAsync();

            if (user == null)
            {
                throw new UserNotFoundException(_localizer);
            }

            if (user.Password != hashedPass)
            {
                throw new UnauthorizedException(_localizer);
            }
            if (user.Status != StatusEnum.ACTIVE)
            {
                throw new AccountInactiveForbiddenException(_localizer);
            }

            Tokens tokens = await SetTokenAsync(user);

            return (user, tokens.Token, tokens.RefreshToken);
        }

        private async Task<Tokens> SetTokenAsync(User user)
        {
            var dd = GetDeviceDetectorConfigured();

            var clientInfo = dd.GetClient();
            var osrInfo = dd.GetOs();
            //var device1 = dd.GetDeviceName();
            var brand = dd.GetBrandName();
            var model = dd.GetModel();

            var claims = GetClaims(user);
            string token = GetToken(claims);
            string refreshToken = GetRefreshToken();
            UserToken t = new UserToken
            {
                AccessToken = token,
                AccessTokenExpiresDateTime = DateTime.UtcNow.AddHours(int.Parse(_configuration.GetSection("BearerTokens")["AccessTokenExpirationHours"])),
                RefreshToken = refreshToken,
                RefreshTokenExpiresDateTime = DateTime.UtcNow.AddHours(int.Parse(_configuration.GetSection("BearerTokens")["RefreshTokenExpirationHours"])),
                UserId = user.Id,

                DeviceModel = model,
                DeviceBrand = brand,

                OS = osrInfo.Match?.Name,
                OSPlatform = osrInfo.Match?.Platform,
                OSVersion = osrInfo.Match?.Version,

                ClientName = clientInfo.Match?.Name,
                ClientType = clientInfo.Match?.Type,
                ClientVersion = clientInfo.Match?.Version
            };
            await _uow.UserTokenRepository.AddAsync(t);
            await _uow.CommitAsync();
            return new Tokens { Token = token, RefreshToken = refreshToken };
        }

        public async Task<(User user, string accessToken, string refreshToken)> LoginSocialAsync(LoginSocialRequest loginSocialRequest)
        {
            FirebaseToken decodedToken = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(loginSocialRequest.Token);
            string uid = decodedToken.Uid;
            UserRecord userRecord = await FirebaseAuth.DefaultInstance.GetUserAsync(uid);
            var pwd = new Password();
            string password = pwd.Next();
            User user = await SignUpAsync(new SignUpRequest{ FullName = userRecord.DisplayName, Email= userRecord.Email, Password=password, Social=true});

            Tokens tokens = await SetTokenAsync(user);

            return (user, tokens.Token, tokens.RefreshToken);

        }

        private string GetRefreshToken()
        {
            var randomNumber = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private string GetToken(IEnumerable<Claim> claims)
        {
            var issuer = _configuration.GetSection("BearerTokens")["Issuer"];
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration.GetSection("BearerTokens")["Key"]));

            var jwt = new JwtSecurityToken(issuer: issuer,
                audience: _configuration.GetSection("BearerTokens")["Audience"],
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddHours(int.Parse(_configuration.GetSection("BearerTokens")["AccessTokenExpirationHours"])),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
            );

            return new JwtSecurityTokenHandler().WriteToken(jwt); //the method is called WriteToken but returns a string
        }

        public async Task LogoutAsync(int userIdValue, string accessToken)
        {
            var tokens = await _uow.UserTokenRepository.FindByAsync(t => t.UserId == userIdValue && t.AccessToken == accessToken);

            foreach (var item in tokens)
            {
                _uow.UserTokenRepository.Delete(item);
            }
            await _uow.CommitAsync();
        }

        public async Task<User> SignUpAsync(SignUpRequest suRequest)
        {
            var existUser = await _uow.UserRepository.FindBy(u => u.Email == suRequest.Email).FirstOrDefaultAsync(); ;
            if (existUser != null)
            {
                if (suRequest.Social)
                {
                    return existUser;
                } else
                {
                    throw new EmailInUseBadRequestException(_localizer);
                }
            } 

            if (string.IsNullOrWhiteSpace(suRequest.Password) ||
                suRequest.Password.Length < 6
                || CheckStringWithoutSpecialChars(suRequest.Password)
                || !CheckStringWithUppercaseLetters(suRequest.Password))
            {
                throw new PasswordRequirementsBadRequestException(_localizer);
            }

            //if (suRequest.Password != suRequest.ConfirmationPassword)
            //{
            //    throw new PasswordsDoesntMatchBadRequestException(_localizer);
            //}

            var passwordHash = GetSha256Hash(suRequest.Password);
            var user = new User
            {
                Email = suRequest.Email,
                FullName = suRequest.FullName,
                BirthDate = suRequest.Birthday,
                Phone = suRequest.Phone,
                Password = passwordHash,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                Gender = (GenderEnum)suRequest.Gender,
                Identity = Suid.NewLettersOnlySuid(),
                Status = StatusEnum.ACTIVE //TODO: the validation is missing now. Fix this once we have the validation process in place.
            };

            await _uow.UserRepository.AddAsync(existUser);

            await _uow.CommitAsync();

            return existUser;
        }

        private bool CheckStringWithoutSpecialChars(string word)
        {
            var regexItem = new Regex("^[a-zA-Z0-9 ]*$");
            return regexItem.IsMatch(word);
        }

        private bool CheckStringWithUppercaseLetters(string word)
        {
            var regexItem = new Regex("[A-Z]");
            return regexItem.IsMatch(word);
        }

        private string GetSha256Hash(string input)
        {
            using (var hashAlgorithm = new SHA256CryptoServiceProvider())
            {
                var byteValue = Encoding.UTF8.GetBytes(input);
                var byteHash = hashAlgorithm.ComputeHash(byteValue);
                return Convert.ToBase64String(byteHash);
            }
        }

        public Task<ClaimsPrincipal> GetPrincipalFromExpiredTokenAsync(string token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false, //you might want to validate the audience and issuer depending on your use case
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration.GetSection("BearerTokens")["Key"])),
                ValidateLifetime = false //here we are saying that we don't care about the token's expiration date
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            SecurityToken securityToken;
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out securityToken);
            var jwtSecurityToken = securityToken as JwtSecurityToken;
            if (jwtSecurityToken == null || !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
                throw new SecurityTokenException("Invalid token");

            return Task.FromResult(principal);
        }

        public async Task<string> GetRefreshTokenAsync(string token, string userId)
        {
            var refreshToken = await _uow.UserTokenRepository.FindBy(u => u.UserId == int.Parse(userId) && u.AccessToken == token && u.RefreshTokenExpiresDateTime > DateTime.UtcNow)
                .FirstOrDefaultAsync();
            return refreshToken?.RefreshToken;
        }

        public async Task<(string accessToken, string refreshToken)> GenerateNewTokensAsync(string token, string refreshToken)
        {
            var oldToken = await _uow.UserTokenRepository.FindBy(u => u.AccessToken == token && u.RefreshToken == refreshToken)
                .Include(u => u.User)
                .FirstOrDefaultAsync();

            if (oldToken == null)
            {
                throw new UnauthorizedException(_localizer);
            }

            var claims = GetClaims(oldToken.User);

            var newToken = GetToken(claims);
            var newRefreshToken = GetRefreshToken();

            oldToken.AccessToken = newToken;
            oldToken.AccessTokenExpiresDateTime = DateTime.UtcNow.AddHours(int.Parse(_configuration.GetSection("BearerTokens")["AccessTokenExpirationHours"]));
            oldToken.RefreshToken = newRefreshToken;
            oldToken.RefreshTokenExpiresDateTime = DateTime.UtcNow.AddHours(int.Parse(_configuration.GetSection("BearerTokens")["RefreshTokenExpirationHours"]));

            _uow.UserTokenRepository.Update(oldToken);
            await _uow.CommitAsync();
            return (newToken, newRefreshToken);
        }

        private List<Claim> GetClaims(User user)
        {
            var issuer = _configuration.GetSection("BearerTokens")["Issuer"];
            var claims = new List<Claim>();
            claims.Add(new Claim(ClaimTypes.Email, user.Email, ClaimValueTypes.Email, issuer));
            claims.Add(new Claim(ClaimTypes.AuthenticationMethod, "bearer", ClaimValueTypes.String, issuer));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, user.FullName, ClaimValueTypes.String, issuer));
            claims.Add(new Claim(ClaimTypes.DateOfBirth, user.BirthDate.ToString(), ClaimValueTypes.Date, issuer));
            claims.Add(new Claim(ClaimTypes.Gender, user.Gender.ToString(), ClaimValueTypes.String, issuer));
            claims.Add(new Claim(ClaimTypes.UserData, user.Id.ToString(), ClaimValueTypes.String, issuer));
            return claims;
        }

        public async Task ChangePasswordAsync(ChangePasswordRequest changePassword, int userId)
        {
            var user = await _uow.UserRepository.FindBy(u => u.Id == userId).FirstOrDefaultAsync();
            var passwordHash = GetSha256Hash(changePassword.OldPassword);

            if (passwordHash != user.Password)
            {
                throw new OldPasswordIncorrectBadRequestException(_localizer);
            }

            if (changePassword.NewPassword != changePassword.ConfirmPassword)
            {
                throw new PasswordsDoesntMatchBadRequestException(_localizer);
            }

            if (string.IsNullOrWhiteSpace(changePassword.NewPassword) ||
                changePassword.NewPassword.Length < 6
                || CheckStringWithoutSpecialChars(changePassword.NewPassword)
                || !CheckStringWithUppercaseLetters(changePassword.NewPassword))
            {
                throw new PasswordRequirementsBadRequestException(_localizer);
            }

            var newPasswordHash = GetSha256Hash(changePassword.NewPassword);

            user.Password = newPasswordHash;
            user.ModifiedAt = DateTime.UtcNow;

            _uow.UserRepository.Update(user);

            await _uow.CommitAsync();
        }

        public async Task GlobalLogoutAsync(int userId)
        {
            var tokens = await _uow.UserTokenRepository.FindByAsync(t => t.UserId == userId);

            foreach (var item in tokens)
            {
                _uow.UserTokenRepository.Delete(item);
            }
            await _uow.CommitAsync();
        }

        private DeviceDetector GetDeviceDetectorConfigured()
        {
            var ua = _detection.UserAgent;

            DeviceDetector.SetVersionTruncation(VersionTruncation.VERSION_TRUNCATION_NONE);

            var dd = new DeviceDetector(ua.ToString());

            // OPTIONAL: Set caching method By default static cache is used, which works best within one
            // php process (memory array caching) To cache across requests use caching in files or
            // memcache add using DeviceDetectorNET.Cache;
            dd.SetCache(new DictionaryCache());

            // OPTIONAL: If called, GetBot() will only return true if a bot was detected (speeds up
            // detection a bit)
            dd.DiscardBotInformation();

            // OPTIONAL: If called, bot detection will completely be skipped (bots will be detected as
            // regular devices then)
            dd.SkipBotDetection();
            dd.Parse();
            return dd;
        }

        public async Task<User> UpdateProfileAsync(UpdateProfileRequest updateProfile, int userId)
        {
            var user = await _uow.UserRepository.FindBy(u => u.Id == userId).FirstOrDefaultAsync();

            if (user == null)
            {
                throw new UserNotFoundException(_localizer);
            }

            user.FullName = updateProfile.FullName;
            user.BirthDate = updateProfile.Birthday;
            user.Phone = updateProfile.Phone;
            user.ModifiedAt = DateTime.UtcNow;
            user.Gender = (GenderEnum)updateProfile.Gender;

            _uow.UserRepository.Update(user);

            await _uow.CommitAsync();

            return user;
        }

        public Task<bool> ValidateTokenAsync(string token)
        {
            var isValid = true;

            var validator = new JwtSecurityTokenHandler();

            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidAudience = _configuration.GetSection("BearerTokens")["Audience"],
                ValidateAudience = true,
                ValidIssuer = _configuration.GetSection("BearerTokens")["Issuer"],
                ValidateIssuer = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration.GetSection("BearerTokens")["Key"])),
                ValidateLifetime = true
            };

            if (validator.CanReadToken(token))
            {
                try
                {
                    SecurityToken securityToken;
                    var principal = validator.ValidateToken(token, tokenValidationParameters, out securityToken);
                }
                catch (Exception)
                {
                    isValid = false;
                }
            }
            else
            {
                isValid = false;
            }

            return Task.FromResult(isValid);
        }

        public async Task<User> GetUserAsync(int userId)
        {
            var user = await _uow.UserRepository.GetAsync(userId);
            if (user == null)
            {
                throw new UserNotFoundException(_localizer);
            }

            return user;
        }

        public async Task ChangeAccountStatusAsync(ChangeAccountStatusRequest changeAccountStatus, int userId)
        {
            var user = await _uow.UserRepository.FindBy(u => u.Identity == changeAccountStatus.Identity).FirstOrDefaultAsync();

            if (user == null)
            {
                throw new UserNotFoundException(_localizer);
            }

            if (user.Id == userId && changeAccountStatus.Active == false)
            {
                throw new AccountDeactivatedForbiddenException(_localizer);
            }

            if (changeAccountStatus.Active == false)
            {
                user.Status = StatusEnum.INACTIVE;
            }
            else
            {
                user.Status = StatusEnum.ACTIVE;
            }

            user.ModifiedAt = DateTime.UtcNow;

            _uow.UserRepository.Update(user);

            await _uow.CommitAsync();
        }

        public async Task<User> UploadAvatar(IFormFile file, int userId)
        {
            var user = await _uow.UserRepository.FindBy(u => u.Id == userId).FirstOrDefaultAsync();

            if (user == null)
            {
                throw new UserNotFoundException(_localizer);
            }

            if (file == null)
            {
                throw new FileNullBadRequestException(_localizer);
            }

            if (file.Length == 0)
            {
                throw new FileNullBadRequestException(_localizer);
            }
            if (file.Length > 2 * 1024 * 1024)
            {
                throw new FileInvalidSizeBadRequestException(_localizer);
            }
            var imagesRootPath = _configuration.GetSection("Blobs")["ImagesRootPath"];
            var imagesContainer = _configuration.GetSection("Blobs")["ImagesContainer"];

            using (Stream stream = file.OpenReadStream())
            {
                using (var binaryReader = new BinaryReader(stream))
                {
                    var fileContent = binaryReader.ReadBytes((int)file.Length);
                    var mime = file.ContentType;
                    if (!mime.Equals("image/png") && !mime.Equals("image/jpg") && !mime.Equals("image/jpeg"))
                    {
                        throw new FileInvalidTypeBadRequestException(_localizer);
                    }

                    string guid = Guid.NewGuid().ToString();

                    if (!string.IsNullOrWhiteSpace(user.Avatar))
                    {
                        //delete the old one in order to avoid client cache problems
                        var segments = new Uri(user.Avatar).Segments;
                        var oldGuid = segments[segments.Length - 1];
                        await RemoveOldImageFromBlobStorage(imagesContainer, oldGuid);
                    }

                    //upload the new one and update user avatar's properties
                    await UploadImageToBlobStorage(fileContent, imagesContainer, guid, mime);
                    user.Avatar = string.Format("{0}/{1}", imagesRootPath, guid);
                    user.AvatarMimeType = mime;

                    await _uow.UserRepository.UpdateAsync(user, userId);
                    await _uow.CommitAsync();
                }
            }

            return user;
        }

        private async Task<string> UploadImageToBlobStorage(byte[] content, string imagesContainer, string fileId, string contentType)
        {
            //// get a reference to our container
            //var container = _blobClient.GetContainerReference(imagesContainer);

            //// using the container reference, get a block blob reference and set its type
            //CloudBlockBlob blockBlob = container.GetBlockBlobReference(fileId);
            //blockBlob.Properties.ContentType = contentType;

            //await blockBlob.UploadFromByteArrayAsync(content, 0, content.Length);

            return "";
        }

        private async Task RemoveOldImageFromBlobStorage(string imagesContainer, string fileId)
        {
            //var container = _blobClient.GetContainerReference(imagesContainer);

            //CloudBlockBlob blockBlob = container.GetBlockBlobReference(fileId);

            //await blockBlob.DeleteIfExistsAsync();
        }

        public async Task<string> ForgotPasswordAsync(string email)
        {
            var user = await _uow.UserRepository.FindBy(u => u.Email == email).FirstOrDefaultAsync();
            if (user == null)
            {
                throw new UserNotFoundException(_localizer);
            }

            string newPass = Guid.NewGuid().ToString();

            var newPasswordHash = GetSha256Hash(newPass);           
           
            user.Password = newPasswordHash;
            user.ModifiedAt = DateTime.UtcNow;
            await _uow.UserRepository.UpdateAsync(user, user.Id);
            await _uow.CommitAsync();
            return newPass;
        }
    }
}