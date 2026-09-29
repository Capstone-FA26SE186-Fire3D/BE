# Checklist Azure App Service va Mailgun

Tai lieu nay dung de chan doan email verification/reset sau khi deploy. HTTP `202` chi co nghia BE da nhan request; job `Sent` chi co nghia Mailgun da chap nhan request. Hai trang thai nay khong chung minh email da vao Inbox.

## 1. Doi chieu ban deploy

1. Xac nhan App Service, resource group, deployment slot va commit/build version dang duoc deploy.
2. Goi `GET /health/version` va xem startup log `FET3D startup configuration`. Log phai cho dung environment, `VerificationHost=fet3d.io.vn` va `EmailWorkerEnabled=True`; khong co token, API key hay dia chi nguoi nhan.
3. Mo `https://fet3d.io.vn/verify-email/` tren browser. Trang nay goi `../api/auth/verify-email` theo relative URL, vi vay origin nay phai cung phuc vu API hoac reverse-proxy `/api` toi BE deploy. Thu mot token test chi sau khi da kiem tra worker.

## 2. App Settings cua Azure

Dat cac gia tri sau trong **App Service > Environment variables > App settings**, luu, sau do restart dung slot. Khong commit API key vao appsettings hoac gui qua chat.

```text
AuthEmail__VerificationUrl=https://fet3d.io.vn
AuthEmail__FrontendUrl=https://fet3d.io.vn
AuthEmail__WorkerEnabled=true
Mailgun__Domain=mg.fet3d.io.vn
Mailgun__From=FET3D <no-reply@mg.fet3d.io.vn>
Mailgun__ApiKey=<production secret stored only in Azure>
Mailgun__BaseUrl=https://api.mailgun.net
APP_VERSION=<commit-or-build-version>
```

Dung endpoint Mailgun dung region cua domain. Domain EU dung endpoint EU thay vi `https://api.mailgun.net`. App settings co tien to `__` ghi de cac key cung ten trong `appsettings.json`; sau khi sua can restart de worker nhan cau hinh moi.

## 3. Kiem tra queue va worker

1. Dang ky/resend mot tai khoan test va giu response `202` cung `traceId`.
2. Trong database dung cua App Service, theo doi job email tu `Pending` sang `Leased`, sau do `Sent`, retry hoac `Dead`.
3. Xem Application Insights/App Service Log Stream theo `JobId`, `Attempt`, `ErrorType`, `ProviderStatusCode` va `ProviderRequestId`. Log khong duoc co recipient, token, API key hoac HTML email.
4. `401`/`403` Mailgun la loi cau hinh/xac thuc va job se `Dead`; `429`, timeout va `5xx` duoc retry theo lease/backoff.
5. Bat **Always On** neu App Service plan ho tro, va kiem tra outbound DNS/HTTPS tu App Service toi Mailgun. Worker chay trong process nen co the dung khi app bi unload neu Always On tat.

## 4. Kiem tra Mailgun

1. Domain `mg.fet3d.io.vn` phai verified va `From` phai thuoc domain nay.
2. Xac nhan API key va API region trung nhau, tai khoan duoc phep gui, va Mailgun sending logs co request ID tu BE.
3. Phan biet `accepted` cua Mailgun voi `delivered`, `rejected`, `bounced`, suppressed va spam trong Mailgun event log va inbox.
4. Gui mot email nghiem thu toi hop thu duoc phep, doi chieu job, Mailgun event va inbox, sau do bam link de xac minh thanh cong.

## Thu tu uu tien cau hinh .NET

Trong Development, User Secrets ghi de `appsettings.json`; environment variables/App Settings va command line co uu tien cao hon. Tren Azure, User Secrets khong duoc deploy: App Settings la cau hinh hieu luc. Vi vay mot gia tri localhost trong Azure App Settings se ghi de gia tri FET3D co trong artifact.
