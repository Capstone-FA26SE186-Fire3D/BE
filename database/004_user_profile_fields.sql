BEGIN;

ALTER TABLE public.users ADD COLUMN IF NOT EXISTS dob date;
ALTER TABLE public.users ADD COLUMN IF NOT EXISTS gender varchar(32);
ALTER TABLE public.users ADD COLUMN IF NOT EXISTS phone_number varchar(32);
ALTER TABLE public.users ADD COLUMN IF NOT EXISTS avatar_url varchar(2048);

COMMIT;
