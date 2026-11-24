-- BE#211: the FireSafetyExpert role. Added on its own, outside a transaction, because PostgreSQL cannot use a new enum label in
-- the transaction that adds it; ExpertContentReview (the next migration) uses it.
ALTER TYPE public.user_role_enum ADD VALUE IF NOT EXISTS 'FireSafetyExpert';
