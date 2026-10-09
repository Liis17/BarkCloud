# Backend — Account deletion outbox

Parent: [[Backend/Users]] · See also: [[Backend/Identity]], [[Backend/SessionRevocation]], [[Shared/SharedLibraries]]

## Назначение

Обеспечивает атомарное сохранение удаления профиля Users вместе с сообщением `UserDeleted`, а также фиксирует локальные транзакционные границы связанных операций Identity.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Users/Features/DeleteAccount/DeleteAccountCommandHandler.cs` | `DeleteAccountCommandHandler` | Транзакция удаления и публикации |
| `Backend/BarkCloud.Users/Infrastructure/UserInfoQueueSender.cs` | `UserDeletedEvent` | Публикация через MassTransit Bus Outbox |
| `Backend/BarkCloud.Users/Program.cs` | `AddEntityFrameworkOutbox` | PostgreSQL Bus Outbox |
| `Backend/BarkCloud.Users/Persistence/Contexts/UsersContext.cs` | `UsersContext` | Inbox/outbox модели и каскадные связи |
| `Backend/BarkCloud.Identity/Consumers/UserDeletedConsumer.cs` | `UserDeletedConsumer` | Очистка Identity |
| `Backend/BarkCloud.Identity/Features/ConfirmResetPassword/ConfirmResetPasswordCommandHandler.cs` | `ConfirmResetPassword` | Локальная транзакция смены пароля/сессий |

## Поведение

`DeleteAccountCommandHandler` открывает транзакцию `UsersContext`, удаляет пользователя с каскадными контактами, устройствами и privacy, публикует `UserDeleted` через scoped `IPublishEndpoint` и коммитит. Bus Outbox сохраняет событие в PostgreSQL; доставку в RabbitMQ выполняет MassTransit. Отказ до фиксации транзакции откатывает и удаление, и событие. Имена и другие профильные события тоже проходят через Bus Outbox, но обновление профиля и запись outbox не объединены общим явным transaction scope.

`UserDeleted` независимо обрабатывают Identity и Files. В Identity consumer в одной транзакции отзывает сессии и удаляет пароль, свойства аутентификации, reset-запросы и коды подтверждения. Очистка других сервисов происходит асинхронно после публикации события.

`ConfirmResetPassword` открывает транзакцию Identity: условно расходует reset, сохраняет новый хеш и создаёт новую пару токенов; при `revoke_other_sessions=true` также удаляет старые refresh и пишет отзывы. После commit ставится уведомление о смене пароля. Ошибка до commit откатывает изменения этой транзакции. Семантика порогов отзыва описана в [[Backend/SessionRevocation]].

## Ограничения и важные детали

Распределённой транзакции между Users, Identity и Files нет. Локальный outbox сохраняет сообщение рядом с изменением Users; обработка в потребителях происходит отдельно. При повторной доставке потребители должны учитывать повтор события.
