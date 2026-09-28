(() => {
  const maxBytes = 5 * 1024 * 1024;
  const allowedTypes = new Set(["image/jpeg", "image/png", "image/webp"]);
  const apiBase = document.querySelector("#apiBase");
  const accessToken = document.querySelector("#accessToken");
  const avatarFile = document.querySelector("#avatarFile");
  const getProfile = document.querySelector("#getProfile");
  const uploadForm = document.querySelector("#uploadForm");
  const uploadButton = document.querySelector("#uploadButton");
  const etagOutput = document.querySelector("#etagOutput");
  const result = document.querySelector("#result");
  const resultSummary = document.querySelector("#resultSummary");
  const resultBody = document.querySelector("#resultBody");
  const avatarLink = document.querySelector("#avatarLink");
  const avatarPreview = document.querySelector("#avatarPreview");

  let profileEtag = "";
  apiBase.value = window.location.origin;

  function baseUrl() {
    return apiBase.value.trim().replace(/\/$/, "");
  }

  function bearerHeaders(extra = {}) {
    const token = accessToken.value.trim();
    if (!token) throw new Error("Bạn cần dán accessToken trước.");
    return { Authorization: `Bearer ${token}`, ...extra };
  }

  async function readResponse(response) {
    const text = await response.text();
    if (!text) return null;
    try { return JSON.parse(text); } catch { return text; }
  }

  function showResult(summary, body, ok = false) {
    result.hidden = false;
    result.classList.toggle("error", !ok);
    result.classList.toggle("success", ok);
    resultSummary.textContent = summary;
    resultBody.textContent = body == null ? "" : JSON.stringify(body, null, 2);
  }

  function resetAvatarResult() {
    avatarLink.hidden = true;
    avatarPreview.hidden = true;
    avatarLink.removeAttribute("href");
    avatarPreview.removeAttribute("src");
  }

  async function loadProfile() {
    resetAvatarResult();
    getProfile.disabled = true;
    try {
      const response = await fetch(`${baseUrl()}/api/auth/me`, { headers: bearerHeaders() });
      const body = await readResponse(response);
      if (!response.ok) {
        showResult(`Không lấy được profile (HTTP ${response.status}).`, body);
        return;
      }

      profileEtag = response.headers.get("ETag") || "";
      etagOutput.textContent = profileEtag ? `ETag: ${profileEtag}` : "API không trả ETag.";
      showResult(`Đã lấy profile (HTTP ${response.status}).`, body, true);
    } catch (error) {
      showResult("Không gọi được API.", { message: error.message });
    } finally {
      getProfile.disabled = false;
    }
  }

  getProfile.addEventListener("click", loadProfile);

  uploadForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    resetAvatarResult();
    const file = avatarFile.files[0];
    if (!file) return showResult("Bạn chưa chọn file ảnh.", null);
    if (!allowedTypes.has(file.type)) return showResult("Sai định dạng ảnh.", { accepted: [...allowedTypes] });
    if (file.size > maxBytes) return showResult("File vượt quá 5 MiB.", { size: file.size, maxBytes });

    if (!profileEtag) {
      await loadProfile();
      if (!profileEtag) return;
    }

    uploadButton.disabled = true;
    try {
      const form = new FormData();
      form.append("file", file, file.name);
      const response = await fetch(`${baseUrl()}/api/me/avatar/upload`, {
        method: "POST",
        headers: bearerHeaders({ "If-Match": profileEtag }),
        body: form
      });
      const body = await readResponse(response);
      if (!response.ok) {
        if (response.status === 412) {
          profileEtag = "";
          etagOutput.textContent = "ETag đã cũ — bấm Lấy ETag rồi thử lại.";
        }
        showResult(`Upload thất bại (HTTP ${response.status}).`, body);
        return;
      }

      profileEtag = response.headers.get("ETag") ||
        (body?.profileRevision != null ? `\"${body.profileRevision}\"` : profileEtag);
      etagOutput.textContent = `ETag: ${profileEtag}`;
      showResult(`Upload thành công (HTTP ${response.status}).`, body, true);
      if (body?.url) {
        avatarLink.href = body.url;
        avatarLink.hidden = false;
        avatarPreview.src = body.url;
        avatarPreview.hidden = false;
      }
    } catch (error) {
      showResult("Không gọi được API.", { message: error.message });
    } finally {
      uploadButton.disabled = false;
    }
  });
})();
