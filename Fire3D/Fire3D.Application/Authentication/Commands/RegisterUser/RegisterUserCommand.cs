using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;
using FirebaseAdmin.Auth;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

public sealed record RegisterUserCommand(string Email, string Password, string FullName) : IRequest<AuthResult<AccountResponse>>;

internal sealed class RegisterUserCommandHandler(IAuthStore store, IPasswordService passwords, TimeProvider clock)
    : IRequestHandler<RegisterUserCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(RegisterUserCommand command, CancellationToken ct)
    {
        // 1. Tạo tài khoản trên Firebase
        try
        {
            var userArgs = new UserRecordArgs
            {
                Email = command.Email,
                Password = command.Password,
                DisplayName = command.FullName
            };
            
            // Hàm này gọi Firebase Admin SDK để tạo user trên Google
            await FirebaseAuth.DefaultInstance.CreateUserAsync(userArgs, ct);
        }
        catch (FirebaseAuthException ex)
        {
            // Nếu email đã tồn tại trên Firebase hoặc mật khẩu yếu
            return AuthResult<AccountResponse>.Fail("FIREBASE_ERROR", ex.Message, 400);
        }

        // 2. Tạo tài khoản tương ứng dưới DB PostgreSQL của hệ thống
        var request = new CreateAccountRequest(command.Email, command.Password, command.FullName, UserRole.Trainee, null);
        
        // Gọi AuthSupport với actor = null để bỏ qua bước check quyền Admin (Cho phép đăng ký tự do)
        return await AuthSupport.CreateAsync(store, passwords, clock, request, null, ct);
    }
}
