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
  const normalizedEmail = () => email.value.trim();

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
    const response = await fetch(url, {
      method: 'POST', headers: jsonHeaders, body: JSON.stringify({ email: normalizedEmail() })
    });
    if (response.status === 429) {
      const seconds = Number.parseInt(response.headers.get('Retry-After'), 10);
      startCooldown(Number.isSafeInteger(seconds) && seconds > 0 ? seconds : 60);
      throw new Error('rate-limited');
    }
    if (!response.ok) throw new Error('request-failed');
    verifyForm.classList.remove('hidden');
    startCooldown();
    otp.focus();
  }

  requestForm.addEventListener('submit', async event => {
    event.preventDefault();
    requestButton.disabled = true;
    try {
      await requestOtp(requestOtpUrl);
      status.textContent = 'Nếu email có thể đăng ký, mã xác minh đã được gửi. Kiểm tra hộp thư của bạn.';
    } catch (error) {
      status.textContent = error.message === 'rate-limited'
        ? 'Bạn đã yêu cầu quá nhiều mã. Vui lòng thử lại sau.'
        : 'Không thể gửi mã lúc này. Vui lòng thử lại sau.';
    } finally {
      requestButton.disabled = false;
    }
  });

  resendButton.addEventListener('click', async () => {
    resendButton.disabled = true;
    try {
      await requestOtp(resendOtpUrl);
      status.textContent = 'Nếu email có thể đăng ký, mã mới đã được gửi. Mã cũ không còn hiệu lực.';
    } catch (error) {
      status.textContent = error.message === 'rate-limited'
        ? 'Bạn đã yêu cầu quá nhiều mã. Vui lòng thử lại sau.'
        : 'Không thể gửi mã lúc này. Vui lòng thử lại sau.';
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
    try {
      const response = await fetch(verifyOtpUrl, {
        method: 'POST', headers: jsonHeaders, body: JSON.stringify({ email: normalizedEmail(), otp: code })
      });
      if (!response.ok) throw new Error('invalid-otp');
      const proof = await response.json();
      sessionStorage.setItem('fet3d.registration.email', normalizedEmail());
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
