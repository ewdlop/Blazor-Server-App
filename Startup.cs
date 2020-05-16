using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BlazorServerApp.Areas.Identity;
using BlazorServerApp.Data;
using BlazorServerApp.Services;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Azure.Cosmos;
using BlazorServerApp.Models;
using Blazored.SessionStorage;
using Microsoft.AspNetCore.Authentication.Cookies;
using BlazorServerApp.Areas.Identity.Data;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using BlazorServerApp.Services.Providers;
using BlazorServerApp.Services.Options;
using Gremlin.Net.Driver;
using Gremlin.Net.Structure.IO.GraphSON;

namespace BlazorServerApp
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(
                    Configuration.GetConnectionString("DefaultConnection")));

            services.AddDefaultIdentity<ApplicationUser>(
                options => {
                    options.SignIn.RequireConfirmedAccount = false;
                    options.Tokens.ProviderMap.Add("CustomEmailConfirmation",
                        new TokenProviderDescriptor(
                            typeof(CustomEmailConfirmationTokenProvider<ApplicationUser>)));
                                options.Tokens.EmailConfirmationTokenProvider = "CustomEmailConfirmation";
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            services.AddTransient<CustomEmailConfirmationTokenProvider<ApplicationUser>>();

            services.ConfigureApplicationCookie(o => {
                o.ExpireTimeSpan = TimeSpan.FromDays(5);
                o.SlidingExpiration = true;
            });
            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider<ApplicationUser>>();
           
            //One client instance per container
            services.AddSingleton<ICosmosDbService<MarvelCharactersResult>>(InitializeCosmosClientInstanceAsync<MarvelCharactersResult>(Configuration.GetSection("CosmosDb"), "MarvelCharactersResult").GetAwaiter().GetResult());
            
            services.AddScoped<IGremlinClient, GremlinClient>((serviceProvider) =>
            {
                var config = serviceProvider.GetRequiredService<IConfiguration>();
                string EndpointUrl = config.GetSection("CosmosDbGreminlin").GetSection("Endpoint").Value;
                string PrimaryKey = config.GetSection("CosmosDbGreminlin").GetSection("PrimaryKey").Value;
                const int port = 443;
                string database = config.GetSection("CosmosDbGreminlin").GetSection("DatabaseName").Value;
                string container = config.GetSection("CosmosDbGreminlin").GetSection("ContainerName").Value;
                GremlinServer gremlinServer = new GremlinServer(EndpointUrl, port, enableSsl: true,
                                    username: "/dbs/" + database + "/colls/" + container,
                                    password: PrimaryKey);
                return new GremlinClient(gremlinServer, new GraphSON2Reader(), new GraphSON2Writer(), GremlinClient.GraphSON2MimeType);
            });
            services.AddSingleton<ICosmosDbGremlinService, CosmosDbGremlinService>();
            services.AddHttpClient();
            services.AddScoped<AppState>();
            services.AddSingleton<IMarvelCharacterService, MarvelCharacterService>();
            services.AddSignalR();

            services.AddAuthorization(options =>
            {
                //options.AddPolicy("Name", policy => policy.RequireClaim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.UniqueName));
                options.AddPolicy("Name", policy => policy.RequireClaim(ClaimTypes.Name));
            });

            //services.AddAuthentication().AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            //{
            //    options.SlidingExpiration = true;
            //    //options.LoginPath = $"/Identity/Account/Login";
            //    //options.LogoutPath = $"/Identity/Account/Logout";
            //    //options.AccessDeniedPath = $"/Identity/Account/AccessDenied";
            //});

            services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(Configuration.GetSection("JWToken").GetSection("Key").Value)),
                    ValidateLifetime = true,
                    ValidateAudience = false,
                    ValidateIssuer = false
                };
            });

            services.AddTransient<IEmailSender, EmailSender>();
            services.Configure<AuthMessageSenderOptions>(Configuration);
            services.AddHttpContextAccessor();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseDatabaseErrorPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                //endpoints.MapControllerRoute(
                //    name: "api", 
                //    pattern: "api/{controller}/{action}/{id?}");
                endpoints.MapControllers();
                endpoints.MapBlazorHub();
                endpoints.MapRazorPages();
                endpoints.MapHub<RealTimeHub>("/chatHub");
                endpoints.MapFallbackToPage("/_Host");
            });
        }

        private async Task<CosmosDbService<T>> InitializeCosmosClientInstanceAsync<T>(IConfigurationSection configurationSection, string containerName)
        {
            string databaseName = configurationSection.GetSection("DatabaseName").Value;
            string account = configurationSection.GetSection("Account").Value;
            string key = configurationSection.GetSection("PrimaryKey").Value;
            CosmosClientBuilder clientBuilder = new CosmosClientBuilder(account, key);
            CosmosClient client = clientBuilder
                                .WithConnectionModeDirect()
                                .Build();
            CosmosDbService<T> cosmosDbService = new CosmosDbService<T>(client, databaseName, containerName);
            DatabaseResponse database = await client.CreateDatabaseIfNotExistsAsync(databaseName);
            await database.Database.CreateContainerIfNotExistsAsync(containerName, "/api_id");

            return cosmosDbService;
        }
    }

}
