using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AuthProfileContractTests
{
    [Fact]
    public void Register_command_exposes_the_required_profile_fields()
    {
        var properties = typeof(RegisterUserCommand).GetProperties().ToDictionary(property => property.Name);

        Assert.Equal(typeof(DateOnly?), properties["Dob"].PropertyType);
        Assert.Equal(typeof(UserGender?), properties["Gender"].PropertyType);
        Assert.Equal(typeof(string), properties["PhoneNumber"].PropertyType);
        Assert.Equal(typeof(string), properties["AvatarUrl"].PropertyType);
    }
}
