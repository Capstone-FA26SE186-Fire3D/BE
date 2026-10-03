(() => {
  const email = document.querySelector('#email');
  const otp = document.querySelector('#otp');
  const requestForm = document.querySelector('#request-form');
  const verifyForm = document.querySelector('#verify-form');
  const requestButton = document.querySelector('#request-button');
  const verifyButton = document.querySelector('#verify-button');
  const resendButton = document.querySelector('#resend-button');
  const status = document.querySelector('#status');
  let cooldownTimer;

  const requestOtpUrl = new URL('../api/auth/registration/request-otp', location.href);
  const resendOtpUrl = new URL('../api/auth/resend-verification', location.href);
  const verifyOtpUrl = new URL('../api/auth/registration/verify-otp', location.href);
  const jsonHeaders = { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' };
  const normalizedEmail = () => email.value.trim().toLowerCase();

  function clearProof() {
    ['email', 'token', 'expiresAt'].forEach(key => sessionStorage.removeItem('fet3d.registration.' + key));
  }

  function requestErrorMessage(error) {
    if (error.message === 'rate-limited') return 'Bạn đã yêu cầu quá nhiều mã. Vui lòng thử lại sau.';
    if (error.message === 'email-changed') return 'Email đã thay đổi. Hãy yêu cầu mã cho email mới.';
    if (error instanceof TypeError || error instanceof SyntaxError)
      return 'Không thể kết nối hoặc đọc phản hồi từ máy chủ. Vui lòng thử lại sau.';
    return error.message;
  }

  email.addEventListener('input', () => {
    clearProof();
    clearInterval(cooldownTimer);
    verifyForm.classList.add('hidden');
    otp.value = '';
    otp.disabled = false;
    verifyButton.disabled = false;
    resendButton.disabled = true;
    status.textContent = 'Email đã thay đổi. Hãy yêu cầu mã xác minh cho email này.';
  });

  function startCooldown(seconds = 60) {
    clearInterval(cooldownTimer);
    let remaining = seconds;
    resendButton.disabled = true;
    resendButton.textContent = 'Gửi lại mã (' + remaining + ' giây)';
    cooldownTimer = setInterval(() => {
      remaining -= 1;
      if (remaining <= 0) {
        clearInterval(cooldownTimer);
        resendButton.disabled = false;
        resendButton.textContent = 'Gửi lại mã';
        return;
      }
      resendButton.textContent = 'Gửi lại mã (' + remaining + ' giây)';
    }, 1000);
  }

  async function requestOtp(url) {
    const requestedEmail = normalizedEmail();
    const response = await fetch(url, {
      method: 'POST', headers: jsonHeaders, body: JSON.stringify({ email: requestedEmail })
    });
    if (normalizedEmail() !== requestedEmail) throw new Error('email-changed');
    if (response.status === 409) {
      clearProof();
      verifyForm.classList.add('hidden');
      const error = await response.json();
      throw new Error(error.errors?.email?.[0] || error.title || 'Email đã được đăng ký. Hãy đăng nhập hoặc đặt lại mật khẩu.');
    }
    if (response.status === 429) {
      const seconds = Number.parseInt(response.headers.get('Retry-After'), 10);
      startCooldown(Number.isSafeInteger(seconds) && seconds > 0 ? seconds : 60);
      throw new Error('rate-limited');
    }
    if (!response.ok) {
      const error = await response.json();
      throw new Error(error.errors?.email?.[0] || error.title || 'Không thể gửi mã lúc này.');
    }
    clearProof();
    otp.disabled = false;
    verifyButton.disabled = false;
    verifyForm.classList.remove('hidden');
    startCooldown();
    otp.focus();
  }

  requestForm.addEventListener('submit', async event => {
    event.preventDefault();
    requestButton.disabled = true;
    try {
      await requestOtp(requestOtpUrl);
      status.textContent = 'Yêu cầu gửi mã đã được nhận. Kiểm tra hộp thư và thư rác của bạn.';
    } catch (error) {
      status.textContent = requestErrorMessage(error);
    } finally {
      requestButton.disabled = false;
    }
  });

  resendButton.addEventListener('click', async () => {
    resendButton.disabled = true;
    try {
      await requestOtp(resendOtpUrl);
      status.textContent = 'Yêu cầu gửi lại mã đã được nhận. Sau cooldown, mã và proof cũ không còn hiệu lực.';
    } catch (error) {
      status.textContent = requestErrorMessage(error);
      if (error.message !== 'rate-limited') resendButton.disabled = false;
    }
  });

  verifyForm.addEventListener('submit', async event => {
    event.preventDefault();
    const code = otp.value.trim();
    if (!/^[0-9]{6}$/.test(code)) {
      status.textContent = 'Nhập đúng mã xác minh gồm sáu chữ số.';
      return;
    }
    verifyButton.disabled = true;
    const verifiedEmail = normalizedEmail();
    try {
      const response = await fetch(verifyOtpUrl, {
        method: 'POST', headers: jsonHeaders, body: JSON.stringify({ email: verifiedEmail, otp: code })
      });
      if (!response.ok) throw new Error('invalid-otp');
      const proof = await response.json();
      if (normalizedEmail() !== verifiedEmail) throw new Error('email-changed');
      sessionStorage.setItem('fet3d.registration.email', verifiedEmail);
      sessionStorage.setItem('fet3d.registration.token', proof.registrationToken);
      sessionStorage.setItem('fet3d.registration.expiresAt', proof.expiresAt);
      status.textContent = 'Email đã xác minh. Quay lại trang đăng ký để hoàn tất thông tin.';
      otp.disabled = true;
      resendButton.disabled = true;
    } catch {
      status.textContent = 'Mã không đúng, đã hết hạn hoặc đã được thay thế. Hãy yêu cầu mã mới.';
      verifyButton.disabled = false;
    }
  });
})();
