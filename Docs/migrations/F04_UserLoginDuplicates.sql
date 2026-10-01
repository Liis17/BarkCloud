-- Выполнить в БД Users перед обновлением сервиса. Оба результата должны быть пустыми.
-- Аккаунты не изменяются; разрешение дублей требует отдельного решения оператора.
SELECT lower("Username") AS normalized_username, array_agg("Id" ORDER BY "Id") AS user_ids
FROM "Users"
GROUP BY lower("Username")
HAVING count(*) > 1
ORDER BY normalized_username;

SELECT lower("Email") AS normalized_email, array_agg("UserId" ORDER BY "UserId") AS user_ids
FROM "UserContacts"
WHERE "Email" <> ''
GROUP BY lower("Email")
HAVING count(*) > 1
ORDER BY normalized_email;
