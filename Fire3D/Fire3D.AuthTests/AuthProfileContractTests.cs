using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AuthProfileContractTests
{
    [Fact]
    public void Token_response_does_not_expose_token_expiry_timestamps()
    {
        var properties = typeof(TokenResponse).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("AccessTokenExpiresAt", properties);
        Assert.DoesNotContain("RefreshTokenExpiresAt", properties);
    }

    [Fact]
    public void Register_command_exposes_the_required_profile_fields()
    {
        var properties = typeof(RegisterUserCommand).GetProperties().ToDictionary(property => property.Name);

        Assert.Equal(typeof(DateOnly?), properties["Dob"].PropertyType);
        Assert.Equal(typeof(UserGender?), properties["Gender"].PropertyType);
        Assert.Equal(typeof(string), properties["PhoneNumber"].PropertyType);
        Assert.Equal(typeof(string), properties["AvatarUrl"].PropertyType);
    }

    [Fact]
    public void Account_response_exposes_profile_fields_for_get_me()
    {
        var properties = typeof(AccountResponse).GetProperties().ToDictionary(property => property.Name);

        Assert.Equal(typeof(DateOnly?), properties["Dob"].PropertyType);
        Assert.Equal(typeof(UserGender?), properties["Gender"].PropertyType);
        Assert.Equal(typeof(string), properties["PhoneNumber"].PropertyType);
        Assert.Equal(typeof(string), properties["AvatarUrl"].PropertyType);
        Assert.Equal(typeof(bool), properties["IsActive"].PropertyType);
        Assert.Equal(typeof(DateTime?), properties["LastLoginAt"].PropertyType);
        Assert.Equal(typeof(DateTime?), properties["EmailVerifiedAt"].PropertyType);
    }

    [Fact]
    public void Profile_schema_migration_has_ef_discovery_metadata()
    {
        var type = typeof(AddUserProfileFields);

        Assert.NotNull(type.GetCustomAttributes(typeof(DbContextAttribute), false).SingleOrDefault());
        var migration = Assert.Single(type.GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>());
        Assert.Equal("20260923120000_AddUserProfileFields", migration.Id);
    }
}
