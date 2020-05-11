using APICore.Data;
using Askmethat.Aspnet.JsonLocalizer.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using Pomelo.EntityFrameworkCore.MySql.Storage;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace APICore.API
{
    public static class ServicesExtensions
    {
        public static void ConfigureDbContext(this IServiceCollection services, IConfiguration config)
        {
            services.AddDbContextPool<CoreDbContext>(
                options => options.UseMySql(config.GetConnectionString("ApiConnection"),
                mySqlOptions =>
                {
                    // replace with your Server Version and Type
                    mySqlOptions.ServerVersion(new Version(5, 7), ServerType.MySql)
                    .CharSetBehavior(CharSetBehavior.AppendToAllColumns)
                    .DisableBackslashEscaping()
                    .CharSet(CharSet.Utf8Mb4);
                }
            ));
        }

        public static void ConfigureSwagger(this IServiceCollection services)
        {
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "API Core",
                    Version = "v1",
                    Description = "API Core"
                });
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey
                });
                var basePath = AppContext.BaseDirectory;
                var fileName = Path.Combine(basePath, "APICore.API.xml");
                var fileName2 = Path.Combine(basePath, "APICore.Common.xml");

                c.IncludeXmlComments(fileName);
                c.IncludeXmlComments(fileName2);
            });
        }

        public static void ConfigureTokenAuth(this IServiceCollection services, IConfiguration config)
        {
            var key = Encoding.UTF8.GetBytes(config.GetSection("BearerTokens")["Key"]);
            services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                x.RequireHttpsMetadata = false;
                x.SaveToken = true;
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAudience = config.GetSection("BearerTokens")["Audience"],
                    ValidateAudience = true,
                    ValidIssuer = config.GetSection("BearerTokens")["Issuer"],
                    ValidateIssuer = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateLifetime = true
                };
            });
        }

        public static void ConfigureCompression(this IServiceCollection services)
        {
            services.Configure<GzipCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Optimal);
            services.AddResponseCompression();
        }

        public static void ConfigureCors(this IServiceCollection services)
        {
            services.AddCors();
        }

        public static void ConfigureI18N(this IServiceCollection services)
        {
            services.AddControllers().AddDataAnnotationsLocalization().AddViewLocalization();
            CultureInfo[] supportedCultures = new[]
                {
                        new CultureInfo("en-US"),
                        new CultureInfo("es-ES")
                };
            services.AddJsonLocalization(options => options.ResourcesPath = "i18n");
            services.Configure<RequestLocalizationOptions>(options =>
            {
                options.DefaultRequestCulture = new RequestCulture(culture: "en-US", uiCulture: "en-US");
                options.SupportedCultures = supportedCultures;
                options.SupportedUICultures = supportedCultures;
            });
        }

        public static void ConfigureHealthChecks(this IServiceCollection services, IConfiguration config)
        {
            services.AddHealthChecks()
                   .AddMySql(config.GetConnectionString("ApiConnection"));
        }

        public static void ConfigureDetection(this IServiceCollection services)
        {
            services.AddDetection();
        }
    }
}